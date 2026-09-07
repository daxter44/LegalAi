# RUNBOOK: spike ścieżki płatniczej Stripe (E3/US-3.1)

Data: 2026-08-28. Branch: `feat/halfvec-retriever`.

**Cel spike'u:** przejść całą ścieżkę subskrypcji w trybie testowym i poznać stany, ZANIM powstanie
cennik i UI. Wynik ma odpowiedzieć na pytanie „czego jeszcze nie wiemy", nie „czy da się kupić".

**Domyślnie wyłączone:** `Billing:Enabled=false` — trasy `/platnosc/*` nie są mapowane. Włączenie
wymaga `Auth:Enabled=true` (plan przypisuje się do konta) oraz kompletu sekretów, inaczej aplikacja
świadomie nie wystartuje.

## Co jest w kodzie

| Trasa | Rola |
|---|---|
| `POST /platnosc/start` | tworzy sesję Checkout i przekierowuje na Stripe (wymaga zalogowania + tokenu antiforgery) |
| `GET /platnosc/powrot` | strona informacyjna po powrocie — **nie nadaje żadnych uprawnień** |
| `POST /platnosc/portal` | Customer Portal: zmiana karty, plan, anulowanie, historia płatności |
| `POST /platnosc/webhook` | **jedyne** miejsce nadające i odbierające plan |

Reguła stanów siedzi w `Services/Billing/SubscriptionSync.cs` (czysta, bez sieci — pokryta testami),
mapowanie ze Stripe w `BillingEndpoints.Map`.

## Krok 1 — konto Stripe (tryb testowy)

1. Załóż konto Stripe i **zostań w trybie testowym** (przełącznik „Test mode" w panelu).
2. Utwórz produkt z ceną cykliczną (miesięczną) → skopiuj identyfikator ceny `price_…`.
3. Skopiuj klucz tajny `sk_test_…` (Developers → API keys).
4. Włącz Customer Portal (Settings → Billing → Customer portal), inaczej `/platnosc/portal` zwróci błąd.

## Krok 2 — Stripe CLI (przekierowanie webhooków na localhost)

```bash
# instalacja: https://docs.stripe.com/stripe-cli
stripe login
stripe listen --forward-to http://localhost:5199/platnosc/webhook
```

Polecenie wypisze sekret podpisu `whsec_…` — **to jest ten, którego trzeba użyć w konfiguracji**
(sekret z panelu dotyczy publicznego adresu, nie przekierowania z CLI).

## Krok 3 — uruchomienie

```bash
Auth__Enabled=true
Email__Provider=log
Billing__Enabled=true
Billing__SecretKey=sk_test_...
Billing__WebhookSecret=whsec_...        # z `stripe listen`
Billing__PriceId=price_...
Billing__PaidPlanId=pro
ASPNETCORE_ENVIRONMENT=Development
dotnet run --project src/PrawoRAG.Api
```

Migracja: `dotnet ef database update` (dochodzi tabela `processed_webhooks` i cztery kolumny
w `AspNetUsers`).

## Krok 4 — co przejść i na co patrzeć

Karta testowa: `4242 4242 4242 4242`, dowolna przyszła data i CVC.
Karta wymagająca uwierzytelnienia: `4000 0025 0000 3155`. Karta odrzucana: `4000 0000 0000 0341`.

- [ ] **Zakup** — po powrocie strona mówi „potwierdzenie może dotrzeć w ciągu kilkunastu sekund",
      a plan pojawia się dopiero po webhooku. To jest zachowanie zamierzone: adres powrotu da się
      otworzyć ręcznie, bez płacenia.
- [ ] **Plan na koncie**: `select "PlanId","PlanStatus","PlanValidUntilUtc","BillingAnchorUtc" from "AspNetUsers";`
      — kotwica okresu powinna przeskoczyć na początek okresu Stripe (limit odnawia się z płatnością).
- [ ] **Powtórzone zdarzenie** — `stripe events resend evt_...`: w logu „już przetworzony", stan bez zmian.
- [ ] **Anulowanie w portalu** — status `canceled`, ale **dostęp zostaje do końca okresu**.
- [ ] **Odrzucona płatność** — `stripe trigger invoice.payment_failed`: status `past_due`, ważność
      przedłużona o `GraceDays`, dostęp działa.
- [ ] **Wygaśnięcie** — `stripe trigger customer.subscription.deleted`: konto spada na plan darmowy.
- [ ] **Podpis** — `curl -X POST localhost:5199/platnosc/webhook -d '{}'` → **400**, nic się nie zmienia.

## Czego ten spike NIE rozstrzyga

- **Cennik i strona zakupu** — kupujemy jednym POST-em, bez UI (to E2/US-2.4).
- **BLIK** — do sprawdzenia, czy obsługuje płatności cykliczne; jeśli nie, potrzebny plan roczny.
- **Faktury, KSeF, VAT** — świadomie poza zakresem całej ścieżki MVP.
- **Okres próbny** — nie tknięty.
- **Monitoring nieudanych webhooków** — na razie panel Stripe.

## Zweryfikowane bez konta Stripe (testy, 838 zielonych)

Reguła stanów: nadanie planu, `past_due` z okresem łaski, rezygnacja do końca okresu, usunięcie
subskrypcji, nieudana pierwsza płatność (brak planu), **zdarzenia spóźnione i nie po kolei**
(`SubscriptionSyncTests`), oraz to, że anulowanie nie odbiera dostępu przed terminem
(`EntitlementsLiveTests`).

**Czego testy nie dowiodą:** samego dialogu ze Stripe — sesji Checkout, podpisu prawdziwego webhooka
i kształtu danych w zdarzeniach. Do tego potrzebne są klucze z Kroku 1 i przejście listy z Kroku 4.
