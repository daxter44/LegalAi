# E1 — Konta, plany i uprawnienia: taski implementacyjne

Data: 2026-08-27. Branch: `feat/halfvec-retriever`. Rozbicie epiku E1 z `PLAN-KOMERCJALIZACJA-EPIKI.md`.

Dokument porządkujący, **nie zlecenie implementacji** — czeka na wyraźne „go". Bez wycen czasowych:
każdy task ma złożoność i ryzyko zakopania się.

## Zasady tego rozbicia

- **Nie ruszamy retrievalu, ingestii ani promptów.** E1 dotyka wyłącznie warstwy API/UI i schematu bazy
  poza korpusem.
- **Kolumna `user_id` w istniejących tabelach zostaje tekstowa i nietknięta.** Zmienia się tylko to, co
  w niej zapisujemy. Wiersze alfy zostają w bazie bez właściciela — ewentualne przypisanie to ręczny
  `UPDATE` po fakcie.
- **`Access:Enabled=false` w produkcji.** Bramka invite nie jest usuwana z kodu; po prostu nie jest
  włączana. Zero pracy, zero ryzyka regresji.
- Testy lokalnie wymagają Postgresa na 5432 i `dotnet ef database update` po zmianach schematu.

## Decyzje do podjęcia zanim ruszy T-1 i T-4

1. **Kontekst EF dla Identity** — rekomendacja w T-1 (rozszerzenie istniejącego), do potwierdzenia.
2. **Dostawca wysyłki poczty** — potrzebny w T-4 i T-5. Przy obietnicy suwerenności warto dostawcę
   z UE (np. Brevo/FR, Mailgun w regionie EU, SES w `eu-central-1`). **[decyzja do podjęcia]** — bez niej
   T-4 i T-5 stoją, reszta E1 idzie dalej.

---

## Blok A — fundament tożsamości

### T-1 · Identity w schemacie i w DI
**Co:** dodać ASP.NET Core Identity do istniejącej bazy. Encja `AppUser` z identyfikatorem GUID,
e-mailem, nazwą wyświetlaną i polami planu (T-8 dołoży ich znaczenie).

**Rekomendacja:** rozszerzyć `PrawoRagDbContext` o Identity (`IdentityDbContext<AppUser>`) zamiast
zakładać drugi kontekst — jedna historia migracji, jeden backup, jedno miejsce konfiguracji.
Konsekwencja: migracja dokłada tabele `AspNet*` do bazy, która trzyma korpus. To akceptowalne (korpus
i tak żyje w tej samej bazie co rozmowy).

**Pliki:** `src/PrawoRAG.Storage/PrawoRagDbContext.cs`, nowa encja w `Entities/`, nowa migracja EF,
`StorageServiceCollectionExtensions.cs`, `Program.cs` (rejestracja Identity).

**Gotowe, gdy:** `dotnet ef database update` przechodzi na lokalnej bazie z korpusem, istniejące migracje
nietknięte, aplikacja startuje.

**Złożoność:** mała. **Ryzyko:** małe — jedyne to konflikt migracji na bazie, która ma już 8+ migracji.

---

### T-2 · Rejestracja, logowanie, wylogowanie
**Co:** trzy strony/endpointy plus polityka hasła. Logowanie na cookie tak jak dziś (`praworag.auth`),
więc konfiguracja ciasteczka z `Program.cs` zostaje.

**Pliki:** `Program.cs` (schemat auth zostaje, dochodzi `SignInManager`), nowe komponenty stron obok
istniejącego `/wejscie`.

**Gotowe, gdy:** można założyć konto, zalogować się, wylogować; wylogowanie kończy obwód Blazora;
niezalogowany na trasie chronionej trafia na logowanie, a `/api/*` dostaje 401 (obecne zachowanie
`OnRedirectToLogin` zachowane).

**Zależy od:** T-1. **Złożoność:** mała. **Ryzyko:** małe.

---

### T-3 · `CurrentUser` zwraca identyfikator konta
**Co:** `CurrentUser.UserId` przestaje zwracać e-mail/nazwę, zaczyna zwracać stabilny identyfikator
konta. Placeholder `demo@local` zostaje wyłącznie dla trybu deweloperskiego bez logowania (albo znika —
do decyzji przy tasku).

**Pliki:** `src/PrawoRAG.Api/Services/CurrentUser.cs` (jedno miejsce), plus przegląd wszystkich
konsumentów `_userId`: `Chat.razor`, `Analiza.razor`, `Szukaj.razor`, `ConversationStore`,
`AnalysisStore`, `FeedbackEntity`.

**Gotowe, gdy:** nowe rozmowy zapisują się pod identyfikatorem konta; użytkownik widzi wyłącznie swoje;
wiersze alfy dalej są w bazie i nie są widoczne dla nikogo (świadome).

**Zależy od:** T-2. **Złożoność:** mała — dzięki rezygnacji z migracji to zmiana jednego zwracanego
stringa. **Ryzyko:** małe, pod warunkiem że przegląd konsumentów jest kompletny.

---

### T-4 · Wysyłka poczty + potwierdzenie adresu e-mail
**Co:** abstrakcja wysyłki (`IEmailSender`), konfiguracja dostawcy z sekretów, link aktywacyjny,
ponowne wysłanie linku, stan „konto niepotwierdzone".

**Decyzja przy tasku:** czy niepotwierdzone konto ma blokadę, czy tylko baner. Rekomendacja: **blokada
zadawania pytań** — inaczej limit darmowy jest za darmo na dowolny zmyślony adres.

**Gotowe, gdy:** rejestracja wysyła list, klik aktywuje konto, wygasły link daje czytelny komunikat.

**Zależy od:** T-2 + decyzja o dostawcy. **Złożoność:** mała. **Ryzyko:** małe (zewnętrzne: konfiguracja
SPF/DKIM na domenie z E2 — bez tego listy lądują w spamie; to zadanie operacyjne, nie kodowe).

---

### T-5 · Reset hasła
**Co:** żądanie resetu, token jednorazowy z terminem ważności, ustawienie nowego hasła, unieważnienie
tokenu po użyciu.

**Gotowe, gdy:** pełny cykl przechodzi; token użyty drugi raz jest odrzucany.

**Zależy od:** T-4 (ta sama wysyłka). **Złożoność:** mała. **Ryzyko:** małe.

---

### T-6 · Ochrona tras i wyłączenie bramki invite w produkcji
**Co:** przegląd, które trasy wymagają zalogowania (dziś część jest za bramką invite), landing i strony
prawne jawnie anonimowe, `Access:Enabled=false` w konfiguracji produkcyjnej.

**Pliki:** `Program.cs` (mapowanie tras, `RequireAuthorization`), `appsettings.Production`.

**Gotowe, gdy:** landing i dokumenty prawne otwarte dla anonimowych; czat, wyszukiwarka, dokument
i analiza tylko dla zalogowanych; `/api/*` bez sesji zwraca 401.

**Zależy od:** T-2. **Złożoność:** mała. **Ryzyko:** małe, ale **wymaga uważnego przeglądu** — to jedyne
miejsce, w którym łatwo przypadkiem odsłonić trasę.

---

### T-7 · Testy tożsamości i izolacji
**Co:** rozszerzyć istniejące `tests/PrawoRAG.Tests/Access` o scenariusze kont: rejestracja i logowanie,
odmowa dla niezalogowanego, **konto A nie widzi rozmów ani analiz konta B**, niepotwierdzone konto nie
zadaje pytań.

**Gotowe, gdy:** testy zielone lokalnie (Postgres na 5432) i nie ma regresji w istniejącym zestawie.

**Zależy od:** T-3, T-6. **Złożoność:** mała. **Ryzyko:** małe.

---

## Blok B — plany i limity

### T-8 · Słownik planów
**Co:** dwa plany (`free`, `pro`) z limitami: zapytania na dzień, okno tempa, budżet znaków wyjścia.
Konfiguracja, nie tabela — dwa plany nie potrzebują CRUD-u. Konto dostaje `free` przy rejestracji.

**Pliki:** nowy `PlansOptions` obok `AccessOptions`, `appsettings.json`, pola planu na `AppUser`.

**Gotowe, gdy:** ręczna zmiana planu konta w bazie zmienia obowiązujące limity.

**Zależy od:** T-1. **Złożoność:** mała. **Ryzyko:** małe.

---

### T-9 · Uprawnienie jako jedyne źródło „czy wolno"
**Co:** jeden serwis odpowiadający na pytanie o dostęp: jaki plan, do kiedy ważny, w jakim stanie
(aktywny / zaległy / anulowany-do-końca-okresu). Wszystkie miejsca pytają tylko jego. Stan i data
ważności trzymane na koncie — **nigdy nie odpytujemy Stripe w ścieżce zapytania**.

**Gotowe, gdy:** wygaszenie ważności planu w bazie natychmiast degraduje konto do darmowego; nic
w ścieżce czatu nie sięga do zewnętrznego dostawcy.

**Zależy od:** T-8. **Złożoność:** mała. **Ryzyko:** małe. **To jest hak, w który wepnie się całe E3.**

---

### T-10 · `CostGuard` na bazie zamiast w pamięci
**Co:** przenieść liczniki z `ConcurrentDictionary` do tabeli zużycia (klucz: konto + data UTC; osobny
wiersz na licznik globalny). Zachować obecny kontrakt (`TryAcquire`/`Record`/`LimitMessage`), żeby
`Chat.razor`, `Analiza.razor` i endpoint SSE nie wymagały przeróbek poza wstrzyknięciem. Progi czytane
z planu konta (T-8) zamiast z jednej wartości globalnej; obecne wartości stają się planem darmowym.

**Uwaga projektowa:** dziś `CostGuard` jest singletonem z blokadą w procesie; przy zapisie do bazy
inkrementacja musi być atomowa po stronie Postgresa (upsert z `+1`), inaczej równoległe zapytania
zgubią zliczenia. To jest cała trudność tego taska.

**Pliki:** `Services/CostGuard.cs`, nowa encja + migracja, `Program.cs` (czas życia serwisu),
`Chat.razor`, `Analiza.razor`, endpoint `/api/chat`.

**Gotowe, gdy:** restart aplikacji nie zeruje dnia; limit darmowy i płatny różnią się realnie;
równoległe zapytania nie przekraczają limitu.

**Zależy od:** T-9. **Złożoność:** średnia — jedyny task w E1 o tej wadze. **Ryzyko:** średnie
(współbieżność, nie sama migracja).

---

### T-11 · Komunikat limitu jako miejsce konwersji
**Co:** `CostGuard.LimitMessage` mówi dziś „to zamknięty test z twardym budżetem" — treść nieaktualna
w produkcie płatnym. Nowa treść: co się wyczerpało, kiedy się odnawia, i (dla planu darmowego) odnośnik
do zakupu. Sam odnośnik podepnie E3; tutaj powstaje miejsce na niego.

**Pliki:** `Services/CostGuard.cs`, `Chat.razor`, `Analiza.razor`.

**Gotowe, gdy:** wyczerpany limit daje komunikat zrozumiały dla płacącego klienta, nie dla testera.

**Zależy od:** T-10. **Złożoność:** trywialna. **Ryzyko:** brak.

---

### T-12 · Testy planów i limitów
**Co:** limit darmowy odcina po progu planu, płatny po swoim; licznik przeżywa restart (test na realnej
bazie); równoległe zapytania nie przepuszczają nadmiaru; wygaśnięcie ważności degraduje plan.

**Zależy od:** T-10. **Złożoność:** mała. **Ryzyko:** małe.

---

## Blok C — minimum produkcyjne należące do E1

### T-13 · Trwałe klucze DataProtection i sekrety poza repo
**Co:** `DataProtection:KeysPath` na wolumenie w konfiguracji produkcyjnej (mechanizm już wpięty
w `Program.cs`), hasła bazy i dostawcy poczty z konfiguracji środowiska, `appsettings.Production`
bez sekretów.

**Gotowe, gdy:** restart aplikacji nie wylogowuje wszystkich; w repo nie ma żadnego sekretu.

**Zależy od:** nic. **Złożoność:** trywialna. **Ryzyko:** małe, ale pominięcie boli od pierwszego
restartu produkcji.

---

### T-14 · Backup bazy z odtworzeniem wykonanym realnie
**Co:** `pg_dump` w harmonogramie, retencja kopii, **jedno odtworzenie faktycznie przeprowadzone**
i opisane. Baza trzyma korpus i konta klientów, więc to jedyny bezpiecznik.

**Uwaga:** korpus to ~94 GB [`PLAN-SIZING-DEPLOY-2026-08-24.md`], więc pełny zrzut jest ciężki. Warto
rozdzielić kopię danych klientów (mała, częsta) od kopii korpusu (duża, rzadka, odtwarzalna z ingestii).

**Gotowe, gdy:** odtworzenie zrobione na czystej instancji i opisane w runbooku.

**Zależy od:** nic. **Złożoność:** mała. **Ryzyko:** średnie — klasyczna wpadka to backup, którego nikt
nie odtworzył.

---

## Kolejność

```
T-1 ─► T-2 ─┬─► T-3 ─┐
            ├─► T-4 ─► T-5
            └─► T-6 ─┴─► T-7

T-1 ─► T-8 ─► T-9 ─► T-10 ─► T-11
                        └──► T-12

T-13, T-14 — niezależne, można w każdej chwili
```

Sensowna kolejność robocza: **T-1 → T-2 → T-3 → T-6 → T-8 → T-9 → T-10 → T-11 → T-7 + T-12**, z T-4/T-5
wpiętymi, gdy zapadnie decyzja o dostawcy poczty, oraz T-13/T-14 na dowolnym luzie.

Po T-9 epik E3 ma już w co się wpiąć — płatności nie muszą czekać na T-10 i dalej.

## Czego w E1 świadomie NIE ma

Zmiana e-maila · usuwanie konta z UI (w MVP na żądanie mailowe) · ekran „zostało Ci X zapytań" ·
funkcje włączane per plan · role · 2FA · SSO · organizacje · CI/CD.
