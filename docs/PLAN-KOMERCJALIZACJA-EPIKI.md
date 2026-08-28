# Komercjalizacja MVP — trzy epiki, cięcie do minimum

Data: 2026-08-27. Branch: `feat/halfvec-retriever`. Kontynuacja `PLAN-KOMERCJALIZACJA.md`.

Dokument porządkujący, **nie zlecenie implementacji**. Następny krok po akceptacji: taski implementacyjne.

## Zasada nadrzędna

**Minimum do deployu.** Każda pozycja jest albo *w MVP*, albo *po deployu* — nie ma trzeciej kategorii.
Kryterium przynależności do MVP jest jedno: **czy bez tego nie da się wpuścić płacącego klienta.**
Wszystko, co „byłoby dobre", czeka.

## Ustalenia wiążące (rozmowa 2026-08-27)

1. **MVP dla prawnika indywidualnego.** Kancelarie, organizacje, role i miejsca — poza zakresem.
2. **Dwa plany, nie więcej:** darmowy i jeden płatny. Roczny wariant ceny to konfiguracja w Stripe,
   nie drugi plan w kodzie.
3. **Bez migracji rozmów z alfy.** Baza produkcyjna powstaje z przeniesienia lokalnej, więc tabele
   `conversations` i `messages` jadą razem z nią. Wymaganie brzmi tylko tyle: **nie skasować i nie
   osierocić tych wierszy**. Testerów jest dwóch — jeśli kiedyś zechcą swoje rozmowy, przypisuje się je
   ręcznym `UPDATE`. Zero kodu, zero skryptu w planie.
   **Konsekwencja upraszczająca:** kolumna `user_id` zostaje typu tekstowego, zmienia się tylko to, co
   w niej zapisujemy (GUID konta zamiast nazwy testera). Dzięki temu US-1.5 nie jest migracją schematu
   całej domeny, tylko zmianą tego, co zwraca `CurrentUser`.
4. **Poza zakresem:** hosting modelu LLM, fakturowanie/KSeF/VAT.

## Trzy epiki

```
E1 Konta, plany i uprawnienia  ──►  E3 Płatności
E2 Marka, UI/UX i zaufanie     ──►  (równolegle; styka się z E3 na cenniku i ekranach zakupu)
```

---

## STAN NA 2026-08-27 (koniec sesji)

| Epik | Stan | Dowód |
|---|---|---|
| **E1 — konta, plany, uprawnienia** | **kod MVP zrobiony**; poza kodem zostaje backup (US-1.12) i ustawienie sekretów przy wdrożeniu | commity `5e8311f` (konta), `8187f49` (plany); 824 testy zielone; runbook `RUNBOOK-KONTA.md` |
| **E2 — marka, UI/UX, zaufanie** | nietknięty | — |
| **E3 — płatności** | nietknięty; hak gotowy | `IEntitlements` czeka na zapis z webhooków |

**Ustalone wartości:** plan darmowy **15 zapytań/okres**, płatny **300**. Do dostrojenia po becie —
świadomie wzięte „na oko", bo brak danych o realnym użyciu.

**Wszystko za flagą `Auth:Enabled` (domyślnie `false`).** Przy wyłączonej fladze aplikacja zachowuje
się dokładnie jak przed sesją: bramka na kody zaproszeń, dobowe limity z alfy, `/rejestracja` daje 404.

**Do zrobienia zanim ruszy E3:** nazwa i domena (E2/US-2.1) — wchodzą do adresów w e-mailach
i do `Auth:PublicBaseUrl`. **Rekomendowany następny krok:** US-3.1, czyli spike całej ścieżki
płatniczej w trybie testowym Stripe, zanim powstanie cennik.

---

# E1 — Konta, plany i uprawnienia ✅ (MVP zrobione)

**Cel:** trwałe konto z niezmiennym identyfikatorem i plan, który realnie steruje limitami — niezależnie
od płatności.

**Gotowe, gdy:** można założyć konto, zalogować się, odzyskać hasło; ręczne nadanie planu w bazie zmienia
limity; liczniki zużycia przeżywają restart.

### W MVP

| # | Story | Gotowe, gdy | Koszt |
|---|---|---|---|
| US-1.1 ✅ | Rejestracja e-mailem i hasłem | ASP.NET Core Identity w istniejącym Postgresie; hasło haszowane; walidacja siły | mały — gotowy mechanizm |
| US-1.2 ✅ | Potwierdzenie adresu e-mail | Link aktywacyjny; wymagany do korzystania | mały (dochodzi wysyłka poczty) |
| US-1.3 ✅ | Logowanie i wylogowanie | Cookie jak dziś; wylogowanie kończy obwód Blazora | mały |
| US-1.4 ✅ | Reset zapomnianego hasła | Token jednorazowy z terminem ważności | mały — bez tego każdy zapomniany login to Twoja ręczna robota |
| US-1.5 ✅ | `UserId` = identyfikator konta (GUID) zamiast czytelnej nazwy | `CurrentUser` zwraca identyfikator konta; kolumna `user_id` bez zmian (tekst); wiersze alfy zostają w bazie, po prostu bez właściciela | mały — zmiana jednego miejsca, nie migracja schematu |
| US-1.7 ✅ | Dwa plany: darmowy i płatny | Słownik planów z limitami; nowe konto dostaje darmowy | mały |
| US-1.8 ✅ | Uprawnienie na koncie jako jedyne źródło „czy wolno" | Plan + ważność + stan (aktywny / zaległy / anulowany-do-końca-okresu); awaria Stripe nie wpływa na zalogowanego | mały |
| US-1.9 ✅ | Liczniki zużycia w bazie | `CostGuard` czyta i zapisuje stan w Postgresie; restart nie zeruje dnia | mały |
| US-1.10 ✅ | Limity odczytywane z planu | Obecne wartości globalne stają się planem darmowym; płatny ma wyższe | mały |
| US-1.11 ✅ (kod) | Trwałe klucze DataProtection i sekrety poza repo — USTAWIENIE ścieżki to krok wdrożeniowy | `DataProtection:KeysPath` na wolumenie — inaczej restart wylogowuje wszystkich | trywialny — mechanizm już wpięty |
| US-1.12 ⬜ NIE ZROBIONE | Backup bazy z odtworzeniem wykonanym realnie (praca po stronie infrastruktury) | `pg_dump` w cronie + jedno odtworzenie zrobione, nie założone | mały |

### Po deployu

Zmiana e-maila i nazwy wyświetlanej · usunięcie konta z poziomu UI (w MVP: **na żądanie mailowe**,
opisane w polityce prywatności — zero kodu, obowiązek spełniony) · funkcje włączane per plan (analiza
zostaje na flagach globalnych) · 2FA · SSO · ekran „zostało Ci X zapytań" (w MVP wystarczy komunikat przy
wyczerpaniu limitu) · wygaszenie bramki invite (`Access:Enabled=false` w produkcji = zero pracy, kod
zostaje).

---

# E2 — Marka, UI/UX i zaufanie

**Cel:** produkt ma nazwę, wygląda spójnie i ma komplet informacji pozwalających go udostępnić.

**Gotowe, gdy:** landing z obietnicą i cennikiem działa; nowe ekrany nie odstają od istniejących;
regulamin, polityka prywatności i oznaczenie treści generowanej są na miejscu.

### W MVP

| # | Story | Gotowe, gdy | Koszt |
|---|---|---|---|
| US-2.1 | Nazwa i domena | Domena `.pl` zarezerwowana; kolizje znaków towarowych sprawdzone | mały (decyzja) |
| US-2.2 | Obietnica i ton | Jedno zdanie oparte na suwerenności danych + zasady tonu | mały (decyzja) |
| US-2.3 | Znak, favicon, dopracowana paleta | Wyprowadzone z istniejących tokenów (`wwwroot/css/tokens.css`) | mały |
| US-2.4 | Landing z obietnicą i cennikiem | Strona statyczna, bez obwodu SignalR; cennik = dwa plany z E1 | średni |
| US-2.5 | Ekrany kont w tym samym języku wizualnym | Rejestracja, logowanie, reset, potwierdzenie — na istniejących tokenach | średni |
| US-2.6 | Uspójnienie tego, co już jest | Wspólne przyciski, pola, karty, stany błędu i ładowania na czterech ekranach — **dopracowanie, nie przebudowa** | średni |
| US-2.7 | Sensowna praca na telefonie | Responsywność; czytelny komunikat przy zerwanym połączeniu zamiast surowego frameworkowego | średni |
| US-2.8 | „Research do weryfikacji, nie porada prawna" | W regulaminie i widoczne w interfejsie | trywialny |
| US-2.9 | Regulamin i polityka prywatności | Opublikowane i podlinkowane; retencja 6 miesięcy (`RetentionService`), przepływ danych, tryb usuwania konta na żądanie | średni (praca poza kodem) |
| US-2.10 | Zapis udzielonych zgód | Zgoda przy rejestracji z datą i wersją dokumentu | mały |
| US-2.11 | AI Act — minimum obowiązkowe | Deklaracja przeznaczenia, potwierdzenie zapoznania z materiałem o systemie, maszynowe oznaczanie treści generowanej (`ANALIZA-AI-ACT.md` §1) | mały–średni; **obowiązek, nie wybór** |

### Po deployu

Przebudowa czterech ekranów na pełny system komponentów · pełna dostępność (kontrast, focus, klawiatura —
w MVP tyle, ile wychodzi z tokenów) · przeniesienie ścieżek marketingowych na renderowanie statyczne poza
landingiem · animacje i dopieszczenia · onboarding nowego użytkownika.

---

# E3 — Płatności

**Cel:** prawnik kupuje jeden płatny plan i sam nim zarządza; my tylko reagujemy na zdarzenia.

**Gotowe, gdy:** w trybie testowym przechodzi: zakup → nadanie planu webhookiem → odrzucona karta →
okres łaski → anulowanie → wygaśnięcie na koniec okresu; zdarzenie powtórzone lub spóźnione nie psuje
stanu konta.

### W MVP

| # | Story | Gotowe, gdy | Koszt |
|---|---|---|---|
| US-3.1 | Spike całej ścieżki w trybie testowym | Zakup i webhook przechodzą end-to-end na sztucznym planie, **zanim** powstanie cennik | mały — rozstrzyga niewiadome najtaniej |
| US-3.2 | Jeden produkt w Stripe = plan płatny z E1 | Cena miesięczna (roczna opcjonalnie, to ta sama pozycja) | trywialny (konfiguracja) |
| US-3.3 | Zakup przez Checkout | Przekierowanie na Stripe, bez własnego formularza kartowego | mały |
| US-3.4 | Webhook jako jedyne źródło prawdy | Weryfikacja podpisu; endpoint poza antiforgery; plan nadawany po zdarzeniu, **nie** po powrocie użytkownika | średni |
| US-3.5 | Odporność na powtórzone i spóźnione zdarzenia | Tabela przetworzonych zdarzeń + porównanie znacznika czasu | mały, ale nieusuwalny — bez tego konta gasną same |
| US-3.6 | Zarządzanie subskrypcją przez Customer Portal | Zmiana karty, anulowanie, historia płatności — gotowa strona Stripe zamiast naszego ekranu | trywialny |
| US-3.7 | `past_due` ≠ anulowany | Okres łaski i jasny komunikat zamiast natychmiastowego odcięcia | mały |
| US-3.8 | Ścieżka płatnicza jako zwykłe endpointy HTTP | Start Checkoutu, powrót i webhook poza komponentami interaktywnymi | mały — decyzja projektowa, nie praca |
| US-3.9 | Link do zakupu przy wyczerpanym limicie | Komunikat limitu prowadzi do Checkoutu zamiast kończyć rozmowę | trywialny |
| US-3.10 | Wdrożenie: Dockerfile, `appsettings.Production`, HTTPS, sekrety | Aplikacja stawia się powtarzalnie; klucze Stripe poza repo | średni |

### Po deployu

BLIK (włączenie w panelu Stripe, ale sprawdzić cykliczność — jeśli nie działa dla subskrypcji, wymaga
planu rocznego) · okres próbny · własne powiadomienia o nieudanej płatności (**Stripe wysyła własne — w MVP
wystarczą**) · monitoring i alarmy nieudanych webhooków (w MVP: panel Stripe pokazuje nieudane dostarczenia)
· CI/CD (w MVP wdrożenie ręczne) · kody rabatowe · faktury.

---

## Kolejność

1. **E1** — ścieżka krytyczna, ale po rezygnacji z migracji rozmów cały epik jest tani: to głównie
   złożenie Identity z istniejącymi limitami.
2. **E2** równolegle — nazwa i teksty prawne mogą powstawać, gdy E1 się pisze; landing potrzebuje
   cennika z E1.
3. **US-3.1** (spike) warto wyciągnąć przed resztą E3, żeby stany subskrypcji nie zaskoczyły na końcu.
4. **E3** po E1.

## Drafting pism — Horyzont 0 zrobiony, Horyzont 1 jako PIERWSZE zadanie po deployu

Decyzja z rozmowy 2026-08-28 (analiza: co system robi przy „przygotuj umowę / wezwanie do zapłaty").
Drafting to inna klasa zadania niż research: źródła są tam OGRANICZENIAMI (elementy konieczne, forma,
terminy), nie treścią odpowiedzi — a w korpusie nie ma wzorów pism, więc bez obsługi zachowanie było
niezdefiniowane (odmowa fałszująca metrykę odmów albo pseudo-dokument poszyty cytatami).

**H0 (zrobione 2026-08-28, w MVP):** `DraftingRequestDetector` (deterministyczny, konserwatywny,
asymetryczny jak `LegalTokenDetector`) → wykrycie omija router i wymusza retrieval → doklejka
`GroundedPrompt.DraftingRules`: jedno zdanie o granicy („nie przygotowuję pism") + checklist wymogów
prawnych dokumentu ze źródłami [n] + sugestia konsultacji z prawnikiem. Każde wykrycie logowane
(`DRAFTING_REQUEST:` w logu ChatService) — **skala tych próśb w becie to dane wejściowe pod H1**.

**H1 (pierwsze zadanie po deployu MVP — albo równolegle z formalnościami: wybór serwera itd.):**
generowanie PROSTYCH pism o zamkniętej strukturze (wezwanie do zapłaty, wypowiedzenie najmu,
odstąpienie od umowy konsumenckiej) — świadomie NIE umowy (otwarta przestrzeń, granica „informacja
prawna vs pomoc prawna"). Schemat: zebranie faktów (dopytanie albo placeholdery `[DO UZUPEŁNIENIA]`,
styk z luką CaseFacts z tematu multi-turn) → retrieval wymogów i podstaw → szkielet pisma →
**przejście weryfikacyjne** gotowego dokumentu względem wymogów ze źródeł (naturalne przedłużenie
groundingu: źródła jako walidator). Warunek wstępny: eval zdolności pisarskiej modeli PL/UE
(Bielik/PLLuM/Gemma) na tym zadaniu — jak przy wyborze modelu per rola. Priorytet typów pism
ustawić po liczbach z licznika `DRAFTING_REQUEST` z bety.

Umowy (Horyzont 2) — poza horyzontem planowania; wracają najwcześniej po H1, zgodnie z kierunkiem
lexedit (research → memo → pisma), który pozostaje ciekawostką kierunkową, nie wyznacznikiem.

---

## Co świadomie wypada z MVP — jednym rzutem oka

Kancelarie i organizacje · więcej niż dwa plany · funkcje per plan · zmiana e-maila · usuwanie konta
z UI · ekran zużycia · 2FA/SSO · przebudowa ekranów na pełny system komponentów · okres próbny · BLIK ·
własne powiadomienia płatnicze · monitoring webhooków · CI/CD · faktury.
