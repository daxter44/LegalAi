# RUNBOOK: włączenie kont użytkowników (E1, bloki A i B)

Data: 2026-08-27. Branch: `feat/halfvec-retriever`. Zakres: T-1…T-12 z `PLAN-KOMERCJALIZACJA-E1-TASKI.md`.

Kod jest kompletny i domyślnie **wyłączony** (`Auth:Enabled=false`) — bez konfiguracji nic się nie
zmienia: dev działa jak dotąd, bramka na kody zaproszeń też.

## Co dokładnie powstało

| Element | Gdzie |
|---|---|
| Konto (`AppUserEntity` : `IdentityUser`) w istniejącym kontekście EF | `src/PrawoRAG.Storage/Entities/AppUserEntity.cs`, `PrawoRagDbContext.cs` |
| Migracja z tabelami `AspNet*` (nic poza nimi nie ruszone) | `Migrations/20260827141301_AddIdentityAccounts.cs` |
| Rejestracja, logowanie, potwierdzenie adresu, reset hasła, wylogowanie | `src/PrawoRAG.Api/Services/Auth/AuthEndpoints.cs` |
| Strony serwerowe (bez Blazora, styl z tokenów) | `Services/Auth/AuthPages.cs` |
| Szablony e-maili HTML + tekst | `Services/Auth/EmailTemplates.cs` |
| Wysyłka: Resend albo log (dev) | `Services/Auth/AppEmailSender.cs` |
| Adresy/tokeny/filtr przekierowań (pokryte testami) | `Services/Auth/AuthLinks.cs` |
| Tożsamość = identyfikator konta | `Services/CurrentUser.cs` |
| Testy kont | `tests/PrawoRAG.Tests/Access/AuthTests.cs` |
| Plany i okres rozliczeniowy | `Services/Plans/PlanOptions.cs`, `Services/Plans/BillingPeriod.cs` |
| Uprawnienie (jedyne źródło „czy wolno") | `Services/Plans/Entitlements.cs` |
| Liczniki zużycia (magazyn + SQL atomowy) | `Services/Plans/UsageCounters.cs`, tabela `usage_counters` |
| Bramka kosztów (dwie osie) | `Services/CostGuard.cs` |
| Testy planów i limitów | `Access/BillingPeriodTests.cs`, `Access/CostGuardRulesTests.cs`, `Access/CostGuardLiveTests.cs` |

## Krok 1 — migracja bazy

```bash
dotnet ef database update --project src/PrawoRAG.Storage --startup-project src/PrawoRAG.Api
```

Migracja **tylko dodaje** siedem tabel Identity. Tabele `documents`, `chunks`, `conversations`,
`messages`, `analyses` i `feedback` pozostają nietknięte.

## Krok 2 — konto w Resend i domena

1. Załóż konto w Resend, dodaj domenę i **zweryfikuj ją** (rekordy DNS: SPF, DKIM oraz DMARC).
   Bez tego listy z potwierdzeniem trafią do spamu albo w ogóle nie wyjdą.
2. Wygeneruj klucz API (uprawnienie: wysyłka).
3. Ustal adres nadawcy, np. `PrawoRAG <noreply@twojadomena.pl>`.

## Krok 3 — zmienne środowiskowe (NIE commitować)

```bash
Auth__Enabled=true
Auth__PublicBaseUrl=https://twojadomena.pl   # KRYTYCZNE w produkcji, patrz niżej
Auth__TermsVersion=2026-08

Email__Provider=resend
Email__ApiKey=re_xxxxxxxxxxxxxxxx
Email__From=PrawoRAG <noreply@twojadomena.pl>
Email__ReplyTo=kontakt@twojadomena.pl        # opcjonalne

DataProtection__KeysPath=/dane/keys          # bez tego restart wylogowuje wszystkich
```

**Dlaczego `PublicBaseUrl` jest krytyczny:** bez niego adres w liście buduje się z hosta żądania, a ten
za reverse proxy pochodzi z nagłówka sterowanego przez klienta. Ktoś mógłby wywołać reset hasła tak, by
ofierze przyszedł list z odnośnikiem na cudzy serwer.

**`Email__Provider=log` poza dev jest odmawiane** — nadawca zastępczy wypisuje wtedy błąd i NIE wysyła
listu (w dev wypisuje treść z odnośnikiem do logu, żeby dało się przejść ścieżkę bez konta u dostawcy).

## Krok 4 — weryfikacja po wdrożeniu

- [ ] `/` pokazuje landing z „Załóż konto" (nie „Mam kod zaproszenia");
- [ ] rejestracja → strona „Sprawdź skrzynkę" → list dochodzi (sprawdź też folder spam);
- [ ] odnośnik potwierdzający działa **raz**, drugie kliknięcie mówi „Adres już potwierdzony";
- [ ] logowanie przed potwierdzeniem adresu → odmowa z czytelnym komunikatem;
- [ ] `/czat` bez logowania → 302 na `/logowanie?powrot=%2Fczat`, po zalogowaniu wraca na `/czat`;
- [ ] `/api/chat` bez ciasteczka → 401 (nie HTML);
- [ ] reset hasła: link jednorazowy, stare hasło przestaje działać;
- [ ] rejestracja na zajęty adres → ta sama strona co przy sukcesie, a właściciel dostaje list
      „ktoś próbował" (ochrona przed wyliczaniem kont);
- [ ] restart aplikacji NIE wylogowuje (czyli `DataProtection__KeysPath` działa).

## Wynik przeglądu bezpieczeństwa (2026-08-27, po implementacji)

Poprawione po przeglądzie — każda pozycja zweryfikowana na uruchomionej aplikacji:

- **Enumeracja przez niepotwierdzone konto.** Identity sprawdza „czy wolno się logować" PRZED
  weryfikacją hasła, więc komunikat „adres niepotwierdzony" wracał przy dowolnym błędnym haśle.
  Teraz pojawia się wyłącznie, gdy pytający zna hasło; przy błędnym — komunikat generyczny.
- **Kanał czasowy przy logowaniu.** Brak konta kończył się odpowiedzią bez kosztu PBKDF2 — stoper
  zdradzał istnienie kont. Teraz nieistniejące konto płaci weryfikację przeciw sztucznemu hashowi.
- **Wyścig przy rejestracji.** `Duplicate*` z `CreateAsync` (drugi wniosek między sprawdzeniem
  a zapisem) pokazywałby błąd zdradzający istnienie konta — teraz zwraca tę samą stronę
  „Sprawdź skrzynkę".
- **Sesje po resecie hasła.** Znacznik bezpieczeństwa jest porównywany z ciasteczkiem co 5 minut
  (domyślnie 30) — ukradziona sesja umiera szybko po zmianie hasła.
- **Serwerowy strop 256 znaków hasła** we wszystkich trzech formularzach — PBKDF2 od megabajtowego
  wejścia to tani DoS, a `maxlength` w HTML niczego nie wymusza.
- **Bezpieczniki startowe:** produkcja z `Auth:Enabled=true` NIE URUCHOMI SIĘ bez `Auth:PublicBaseUrl`
  albo z `Email:Provider=log` (zweryfikowane: wyjątek przy starcie).

Znane i zaakceptowane (świadomie bez zmiany kodu):

- Komunikat o blokadzie konta (po 5 nieudanych próbach) zdradza istnienie konta — wartość informacji
  dla prawowitego użytkownika wygrywa; atakujący i tak wcześniej wpada w limiter `auth`.
- Potwierdzenie adresu zmienia stan na GET — skanery pocztowe klikające odnośniki mogą potwierdzić
  konto za użytkownika. Powszechny kompromis; alternatywa (przycisk POST na stronie z odnośnika)
  do rozważenia przy E2.
- **Limiter `auth` kluczuje po adresie IP z połączenia.** Za reverse proxy bez skonfigurowanych
  forwarded headers WSZYSCY klienci mają adres proxy — jeden bot wyczerpuje limit wszystkim.
  Przy wdrożeniu za proxy (E8/T-3.10) trzeba dodać `UseForwardedHeaders` z jawną listą zaufanych
  proxy — bez listy zaufanych ten nagłówek sam staje się wektorem (klient wpisuje dowolny adres).

## Decyzje bezpieczeństwa (nie „upraszczać" bez powodu)

1. **Antiforgery walidowane jawnie** w każdym POST — minimalne API czyta `Request.Form`, więc
   automatyczna walidacja go nie obejmuje. Pole nazywa się `__RequestVerificationToken`, bo tego
   oczekuje walidator.
2. **Jeden komunikat na wszystkie błędy logowania** oraz identyczna odpowiedź przy rejestracji na
   zajęty adres i resecie nieznanego adresu — formularze nie mogą służyć do sprawdzania, kto ma konto.
3. **Blokada konta po 5 nieudanych próbach na 15 minut** + limiter HTTP `auth` (12 żądań/min na adres IP)
   na wszystkich ścieżkach kont.
4. **Wymagany potwierdzony adres** przed pierwszym logowaniem.
5. **Hasło: minimum 10 znaków**, bez wymuszania znaków specjalnych (długość chroni lepiej).
6. **Tylko lokalne przekierowania** po zalogowaniu — `powrot=//obcy.host` jest odrzucany.
7. **Wylogowanie POST-em** z tokenem; `GET /wylogowanie` pokazuje wyłącznie potwierdzenie.
8. **Reset hasła unieważnia stare sesje** (zmiana znacznika bezpieczeństwa konta).
9. **Odnośniki z e-maili ważne 6 h i jednorazowe.**
10. **Tokeny nigdy nie trafiają do logu** poza trybem dev z nadawcą zastępczym.

## Rozmowy z alfy

Wiersze sprzed kont mają w kolumnie `user_id` nazwę testera, więc po włączeniu kont **nie są widoczne
w aplikacji** — leżą w bazie nietknięte. Gdyby któryś z dwóch testerów chciał odzyskać swoją historię,
po założeniu przez niego konta wystarczy jednorazowo:

```sql
-- najpierw sprawdź, co jest do przeniesienia i pod jakim identyfikatorem jest nowe konto
select distinct "UserId" from conversations;
select "Id", "Email" from "AspNetUsers";

update conversations set "UserId" = '<identyfikator konta>' where "UserId" = 'Jan Kowalski';
update analyses      set "UserId" = '<identyfikator konta>' where "UserId" = 'Jan Kowalski';
```

## Plany i limity (blok B)

**Dwie osie, których nie wolno pomieszać:**

| Oś | Co ogranicza | Skąd wartości | Okres |
|---|---|---|---|
| Rozliczeniowa | ile należy się KLIENTOWI | plan konta (`Plans:Items`) | okres rozliczeniowy konta |
| Pojemnościowa | ile zniesie NASZ SPRZĘT | `Access:MaxGlobal*` | doba UTC |

Domyślne plany: **darmowy 15 zapytań/okres**, **pro 300**. Zmiana to konfiguracja, nie migracja:

```bash
Plans__Items__free__RequestsPerMonth=15
Plans__Items__pro__RequestsPerMonth=300
```

**Okres rozliczeniowy liczy się od dnia miesiąca, w którym powstało konto**, nie od pierwszego
kalendarzowego. Powód: Stripe rozlicza w okresach od dnia zakupu, więc kalendarzowy miesiąc trzeba by
przy płatnościach przepisywać na żywych licznikach. E3 ustawi `BillingAnchorUtc` na początek okresu
subskrypcji i limit zacznie odnawiać się razem z płatnością. Konto z kotwicą 31. dostaje w lutym
ostatni dzień miesiąca i wraca na 31. w marcu.

**Nadanie planu ręcznie** (do czasu E3) działa od następnego zapytania, bez restartu:

```sql
update "AspNetUsers" set "PlanId" = 'pro' where "Email" = 'ktos@kancelaria.pl';
-- plan czasowy: dodatkowo "PlanValidUntilUtc"; po tej dacie konto samo spada na darmowy
```

**Liczniki** siedzą w tabeli `usage_counters` (klucz: zakres + konto + początek okresu). Nowy okres to
nowy wiersz, więc zerowanie dzieje się samo — nie ma żadnego zadania w tle, które musiałoby zdążyć.
Stare wiersze zostają jako historia zużycia. Zliczanie jest atomowe po stronie Postgresa (warunkowy
upsert), bo dwie karty przeglądarki tego samego użytkownika nie mogą przepchnąć się ponad limit.

**Tryb bez kont zachowany bit w bit:** przy `Auth:Enabled=false` planów nie ma, a limit dobowy per
tester z `Access:MaxUserRequestsPerDay` działa jak w alfie.

### Weryfikacja po wdrożeniu (blok B)

- [ ] po wyczerpaniu limitu komunikat mówi, ILE było, KIEDY się odnowi i (na darmowym) że jest Pro;
- [ ] restart aplikacji NIE zeruje wykorzystanego limitu;
- [ ] `update "AspNetUsers" set "PlanId"='pro'` działa bez restartu;
- [ ] wyczerpany globalny cap dobowy NIE zjada zapytania z pakietu klienta (zwrot rezerwacji).

## Czego tu jeszcze nie ma (świadomie)

Ekran zużycia w interfejsie (dane są — `CostGuard.UsageAsync` — brakuje widoku) · funkcje włączane
per plan · zmiana adresu e-mail · usuwanie konta z interfejsu (w MVP na żądanie mailowe, opisane
w polityce prywatności) · cokolwiek związanego z płatnościami (E3).
