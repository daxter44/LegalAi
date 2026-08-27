# Plan komercjalizacji — branding, UI/UX, konta, subskrypcje Stripe

Data: 2026-08-27. Branch: `feat/halfvec-retriever`.

Dokument porządkujący, **nie zlecenie implementacji**. Żadna linia kodu nie powstała przy jego pisaniu.
Każde twierdzenie ma etykietę:

- **[ustalone z kodu]** — sprawdzone w repo, ze wskazaniem pliku.
- **[decyzja do podjęcia]** — rozwidlenie, które trzeba rozstrzygnąć, zanim cokolwiek powstanie.
- **[rekomendacja]** — moja propozycja z uzasadnieniem; do przyjęcia albo odrzucenia.
- **[ryzyko]** — rzecz, która może wywrócić plan, jeśli ją przemilczymy.

---

## 1. Podsumowanie dla niecierpliwych

Trzy rzeczy, o które pytasz (branding/UI, konta, płatności), to **jedna zależność łańcuchowa i dwa
tory równoległe**:

```
TOR A (blokujący, sekwencyjny):  tożsamość → uprawnienia (plan) → rozliczenia (Stripe) → faktury
TOR B (równoległy, niezależny):  branding (nazwa/domena/znak) → system UI → przebudowa ekranów
TOR C (brama przed sprzedażą):   regulamin + RODO + AI Act + model w UE  ← BEZ TEGO NIE SPRZEDAJEMY
```

Kolejność w torze A wynika z jednego faktu technicznego: **dziś nie ma pojęcia „użytkownik"** — jest kod
zaproszenia mapowany na nazwę testera, a ta nazwa jest kluczem w całej domenie [ustalone z kodu:
`Services/AccessOptions.cs`, `Services/CurrentUser.cs:20` — `UserId` to e-mail/nazwa z claimów z
fallbackiem `demo@local`; rozmowy, analizy i feedback filtrowane po tym stringu]. Stripe nie ma czego
subskrybować, dopóki nie istnieje trwałe konto z identyfikatorem, który się nie zmienia. Tego porządku
nie da się odwrócić.

Natomiast **branding i UI nie czekają na nic** — można je robić od jutra, równolegle.

---

## 2. Stan faktyczny (audyt repo, 2026-08-27)

Co JEST i da się na tym budować:

| Element | Gdzie | Uwaga |
|---|---|---|
| Bramka dostępu na kody zaproszeń + cookie 30 dni | `Program.cs` (cookie auth), `AccessOptions.cs` | Tożsamość = nazwa testera z konfiguracji |
| Izolacja danych per użytkownik (rozmowy, analizy, feedback) | `ConversationStore`, `AnalysisStore` | Działa, pokryte `AccessGateTests` |
| Twarde limity: dzienne per user + globalne + cap znaków LLM | `Services/CostGuard.cs` | **Gotowy szkielet limitów planu** |
| Limit tempa (okno minutowe) | `Services/RateGuard.cs` | j.w. |
| System projektowy w tokenach CSS | `wwwroot/css/tokens.css` | Kolory, typografia, spacing, cienie, focus — już zmienne |
| Blazor Server, jeden host, serwisy przez DI bez skoku HTTP | `Program.cs` | UI i API to jeden proces |
| Nagłówki bezpieczeństwa + CSP + rate limiter HTTP | `Program.cs` | CSP `script-src 'self'` — istotne dla Stripe, §5.4 |
| DataProtection z trwałymi kluczami (opcja) | `Program.cs`, `DataProtection:KeysPath` | Warunek trwałości logowań po restarcie |

Czego NIE MA: rejestracji, haseł i resetu hasła, weryfikacji e-maila, tabeli użytkowników, pojęcia planu
i uprawnień, jakiejkolwiek integracji płatniczej, faktur, regulaminu, polityki prywatności, Dockerfile'a
aplikacji, CI/CD, backupów, monitoringu [ustalone z kodu + `PLAN-STRATEGIA-PILOTAZ.md` §1].

Liczniki `CostGuard` są **w pamięci procesu** — restart zeruje dzień [ustalone z kodu]. Przy płatnym
planie to przestaje być akceptowalnym kompromisem: limit, za który ktoś zapłacił, musi przeżyć restart.

---

## 3. Tor A1 — tożsamość i konta

### 3.1 Rozwidlenie: własne konta czy zewnętrzny dostawca tożsamości

| Opcja | Za | Przeciw |
|---|---|---|
| **ASP.NET Core Identity w naszym Postgresie** | jedna baza (już mamy), jeden backup, zero abonamentu, dane w naszej infrastrukturze — zgodne z obietnicą suwerenności; EF Core już w stacku | my odpowiadamy za hasła, reset, weryfikację e-maila, 2FA |
| **Keycloak / Logto self-host** | gotowe SSO, 2FA, federacja — przyda się przy sprzedaży kancelariom | drugi serwis do utrzymania, drugi backup, więcej RAM na serwerze |
| **Auth0 / Clerk / Cognito** | najmniej pracy | **konflikt z pozycjonowaniem**: dane osobowe klientów-prawników u dostawcy z USA, przy produkcie sprzedawanym hasłem suwerenności [ryzyko] |

**[rekomendacja]** ASP.NET Core Identity we własnym Postgresie. Powód nie jest ideologiczny, tylko
operacyjny: solo-dev utrzymuje jedną bazę i jeden backup, a rejestr użytkowników to kilkaset rekordów,
nie system. Keycloak dokładamy dopiero wtedy, gdy pojawi się realny klient z wymogiem logowania firmowego
(SSO) — i wtedy Identity zostaje jako lokalny provider obok.

### 3.2 Migracja tożsamości — to jest prawdziwa robota, nie sama rejestracja

**[ryzyko]** Dziś `UserId` to **czytelna nazwa** („Jan Kowalski" albo e-mail), zapisana bezpośrednio
w wierszach rozmów, analiz i feedbacku. Naiwne wprowadzenie kont daje:

- zmiana e-maila przez użytkownika = utrata jego historii;
- dwa konta o tej samej nazwie = zlanie rozmów (dziś ostrzega o tym runbook bramki);
- RODO: prawo do usunięcia konta wymaga skasowania rozproszonego stringa, nie jednego rekordu.

Właściwe rozwiązanie: **klucz techniczny (GUID) jako `UserId`**, e-mail jako atrybut konta. To oznacza
migrację danych istniejących testerów (mapowanie nazwa → nowy identyfikator) i przejście przez wszystkie
miejsca, które dziś filtrują po stringu. Złożoność: średnia. Ryzyko zakopania się: małe — **ale tylko
jeśli zrobimy to PRZED betą**. Po becie mamy realne dane klientów i ta sama migracja staje się operacją
z ryzykiem utraty cudzej historii.

**[decyzja do podjęcia]** Czy rozmowy testerów alfy mają przetrwać przejście na konta? Jeśli nie —
migracja jest trywialna (czyścimy i startujemy od zera). Jeśli tak — to osobne zadanie z kopią zapasową
i testem odtworzenia.

### 3.3 Kim jest klient: osoba czy kancelaria

**[decyzja do podjęcia]** — i ma większy wpływ na model danych niż wybór dostawcy płatności.

- **B2C (prawnik indywidualny)**: konto = subskrybent. Model prosty.
- **B2B (kancelaria z miejscami)**: potrzebna encja *organizacji*, zapraszanie członków, role
  (właściciel/członek), rozliczenie per miejsce, jedna faktura na firmę. To istotnie więcej pracy
  i przenika do izolacji danych (czy wspólnicy widzą nawzajem swoje rozmowy? domyślnie NIE).

**[rekomendacja]** Start B2C, ale **zaprojektować schemat z organizacją od początku**: konto zawsze
należy do organizacji, przy B2C jednoosobowej i niewidocznej w UI. Dołożenie organizacji później to
druga migracja tożsamości — a pierwszej (§3.2) właśnie nie chcemy powtarzać na żywym ruchu. Koszt teraz:
jedna tabela i jedna kolumna. Koszt później: powtórka §3.2 na danych klientów.

Kanałem sprzedaży ma być shiftlaw.pl z 590 prawnikami [`PLAN-STRATEGIA-PILOTAZ.md`] — część z nich to
kancelarie, nie jednoosobowe działalności. To argument, żeby pomyśleć o tym teraz, a nie po fakcie.

---

## 4. Tor A2 — uprawnienia (plany) jako warstwa NIEZALEŻNA od Stripe

Najczęstszy błąd w takich wdrożeniach: pytanie „czy user ma dostęp" trafia do Stripe. Wtedy awaria albo
opóźnienie u dostawcy płatności = produkt nie działa.

**[rekomendacja]** Rozdzielić trzy pojęcia i trzymać je u siebie:

1. **Plan** (nasz słownik): `free` / `pro` / `kancelaria` — nazwa, limity, włączone funkcje.
2. **Uprawnienie (entitlement)** przypisane do konta: jaki plan, do kiedy ważny, w jakim stanie
   (aktywny / okres próbny / zaległy / anulowany-do-końca-okresu).
3. **Subskrypcja u dostawcy**: identyfikatory Stripe, wyłącznie jako **źródło zdarzeń** aktualizujących
   punkt 2.

Cała aplikacja pyta wyłącznie o punkt 2. Stripe może paść, a zalogowany klient dalej pracuje do końca
opłaconego okresu.

**Efekt uboczny: limity już mamy.** `CostGuard` egzekwuje dziś dzienny limit zapytań per user i globalny
cap znaków [ustalone z kodu]. Plan płatny to w praktyce **te same limity odczytywane z planu konta**
zamiast z jednej wartości globalnej. Nie budujemy nowego mechanizmu — parametryzujemy istniejący.
Realnie do zrobienia: przenieść liczniki z pamięci do bazy (§2) i czytać progi z planu.

Złożoność: mała. To najtańsza część całego przedsięwzięcia i jednocześnie ta, która **musi powstać przed
Stripe** — webhook musi mieć co ustawiać.

---

## 5. Tor A3 — Stripe

### 5.1 Co konkretnie z oferty Stripe

**[rekomendacja]** Najmniejszy sensowny zestaw, bez własnego formularza kartowego:

- **Stripe Checkout** (przekierowanie na stronę Stripe) — nie dotykamy danych karty, więc zakres
  obowiązków PCI spada do minimum. Własny formularz kartowy to wielokrotnie więcej pracy i odpowiedzialności.
- **Customer Portal** (gotowa strona Stripe) — zmiana karty, zmiana planu, anulowanie, historia płatności.
  Zastępuje cały ekran „moja subskrypcja", którego dzięki temu nie budujemy.
- **Webhooki** — jedyne źródło prawdy o stanie płatności. Po powrocie z Checkoutu **nie ufamy** temu, że
  użytkownik wrócił na adres sukcesu; uprawnienie nadaje dopiero potwierdzone zdarzenie.

Metody płatności: karta + **BLIK** — na polskim rynku jego brak to realna strata konwersji.
**[do weryfikacji]** czy BLIK w Stripe obsługuje płatności cykliczne, czy tylko jednorazowe; jeśli tylko
jednorazowe, to argument za planem rocznym płatnym z góry.

### 5.2 Trzy rzeczy, które w takich wdrożeniach zawsze bolą

1. **Idempotencja webhooków.** Stripe dostarcza zdarzenia *co najmniej raz* i nie zawsze w kolejności.
   Bez tabeli przetworzonych zdarzeń i porównania znacznika czasu spóźnione „anulowano" potrafi wyłączyć
   konto, które właśnie przedłużyło subskrypcję.
2. **Weryfikacja podpisu webhooka** i świadome wyłączenie tego jednego endpointu z antiforgery — inaczej
   każdy nada sobie plan `pro` zwykłym POST-em.
3. **Stan „zaległy" (`past_due`) to nie „anulowany".** Odrzucona karta = kilka dni na ponowienie. Twarde
   odcięcie w tym stanie generuje wściekłe zgłoszenia od ludzi, którzy zapłacili.

### 5.3 Faktury i VAT — najbardziej niedoceniany element [ryzyko]

Prawnicy **będą potrzebowali faktury VAT na działalność**; to warunek zakupu, nie udogodnienie.

- **KSeF.** Krajowy System e-Faktur wchodzi etapami w 2026 r. Faktury Stripe **nie są** polską fakturą
  ustrukturyzowaną. Praktycznie oznacza to integrację z polskim systemem fakturowym (Fakturownia / inFakt /
  wFirma) jako warstwą wystawiania, przy Stripe wyłącznie jako procesorze płatności.
  **[do weryfikacji z księgowością — nie zgaduję zakresu i harmonogramu obowiązku]**
- **VAT OSS / miejsce świadczenia** przy sprzedaży poza PL. Jeśli sprzedajemy tylko w PL, problem znika.
  **[decyzja do podjęcia]**
- **Podmiot sprzedający**: JDG czy spółka. Wpływa na regulamin, odpowiedzialność i na to, kto zakłada
  konto Stripe. **[decyzja do podjęcia]**

**[ryzyko]** To jedyny element planu nierozstrzygalny inżyniersko — wymaga księgowej. Warto ruszyć z nim
równolegle z kodem, bo blokuje pierwszą płatność, a nie pierwszą linijkę.

### 5.4 Dwa szczegóły wynikające wprost z naszego stacku

- **CSP.** Nagłówek ma dziś `script-src 'self'` i `form-action 'self'` [ustalone z kodu: `Program.cs`].
  Checkout jako przekierowanie przejdzie (to nawigacja), ale osadzanie komponentów Stripe na stronie
  wymagałoby świadomego poluzowania CSP. Kolejny argument za Checkoutem zamiast osadzania.
- **Blazor Server a powrót z płatności.** Powrót ze Stripe to nowe załadowanie strony i nowy obwód
  SignalR. Ścieżka płatnicza (start Checkoutu, powrót, webhook) powinna być **zwykłymi endpointami HTTP**,
  a nie logiką w komponencie interaktywnym — inaczej rozjazd stanu obwodu będzie źródłem trudnych błędów.

---

## 6. Tor B — branding i UI/UX

### 6.1 Branding to nie logo

Do rozstrzygnięcia w tej kolejności, bo każdy punkt zależy od poprzedniego:

1. **Nazwa i domena.** „PrawoRAG" to nazwa robocza — „RAG" nic nie znaczy dla prawnika i brzmi jak
   narzędzie dla inżynierów. **[decyzja do podjęcia]** Sprawdzenie domeny `.pl` **oraz** kolizji znaków
   towarowych (UPRP/EUIPO) — przy produkcie dla prawników wpadka nazewnicza jest wyjątkowo kosztowna
   wizerunkowo.
2. **Obietnica (positioning).** Wyróżnik jest już ustalony w strategii: **suwerenność danych** — cały stos
   w PL/UE, pytania nie wychodzą do amerykańskich dostawców [`PLAN-STRATEGIA-PILOTAZ.md` §0.4]. To zdanie
   powinno stać na landingu nad zgięciem: nikt na GPT/Claude go nie powie.
3. **Ton.** Powściągliwy, oparty na dowodach (cytat + link do źródła), bez „AI, które odpowie na wszystko".
   Nasza uczciwa odmowa jest cechą sprzedażową, nie wadą — warto ją pokazać wprost.
4. **Znak i paleta.** Dopiero tutaj. Obecna paleta (atrament/granat `#1f3a8a`) jest dla tej kategorii
   bezpieczna i sensowna — nie ma powodu jej wyrzucać, jest powód ją dopracować.

### 6.2 „Atrapa UI" — co z nią zrobić

**[ustalone z kodu]** Podstawy są lepsze, niż sugeruje słowo „atrapa": jest system tokenów CSS (kolory,
typografia, spacing, cienie, focus), są cztery realne ekrany (czat, szukaj, dokument, analiza), jest
layout. Brakuje **dopracowania**, nie fundamentu.

**[rekomendacja]** **Nie przepisywać na React/Next.** Przepisanie oznacza oderwanie UI od serwisów
wołanych dziś przez DI bez skoku HTTP, więc trzeba by zbudować pełne publiczne API z autoryzacją plus
drugi pipeline budowania — i to dla ekranów, których główną trudnością jest strumieniowanie odpowiedzi
i klikalne cytaty, a nie bogactwo interakcji. Złożoność: duża. Wartość dla klienta: żadna. Nowoczesny,
spójny wygląd to kwestia projektu graficznego, nie frameworka.

**[ryzyko]** Blazor Server ma jednak realny koszt przy ruchu publicznym: każdy użytkownik trzyma otwarte
połączenie SignalR i stan sesji w pamięci serwera, a zerwanie sieci na telefonie daje ekran „ponawiam
połączenie". Przy dziesiątkach osób to nieistotne; przy setkach trzeba to zmierzyć. Tani ruch pośredni:
przełączyć **strony marketingowe i ścieżkę płatniczą na renderowanie statyczne** (bez obwodu), zostawiając
interaktywność czatowi i analizie. Mały koszt, wyraźny zysk.

### 6.3 Czego brakuje w UI, gdy produkt staje się płatny

Ekrany, których dziś **nie ma**, a bez których nie da się sprzedawać: landing z obietnicą i cennikiem ·
rejestracja / logowanie / reset hasła · potwierdzenie e-maila · ekran planu i zużycia („zostało Ci X
zapytań dziś") · powrót z płatności (sukces / porażka / oczekiwanie na potwierdzenie) · regulamin
i polityka prywatności · usunięcie konta (RODO) · komunikat po wyczerpaniu limitu.

Ostatni punkt jest ważniejszy, niż wygląda: dziś wyczerpany limit to komunikat o błędzie [ustalone
z kodu: `CostGuard`]. W produkcie płatnym to **moment konwersji na wyższy plan**.

---

## 7. Tor C — brama przed pierwszą złotówką

Rzeczy, które blokują **sprzedaż**, nie kod. Łatwo je odłożyć i obudzić się z gotowym produktem, którego
nie wolno udostępnić.

1. **Model LLM w UE.** Alfa działa na Gemmie przez Google AI Studio (hosting US) — świadomie, tylko dla
   przyjaznych testerów, uprzedzonych [`RUNBOOK-LAUNCH-ALFA.md` krok 3]. **Sprzedaż publiczna pod hasłem
   suwerenności przy modelu w USA jest nie do obrony** — ani wobec klienta, ani wobec RODO. Przejście na
   hosting UE (Sherlock/CloudFerro) jest więc **warunkiem włączenia płatności**, nie ulepszeniem.
   [ryzyko — największy pojedynczy blocker w tym dokumencie]
2. **Regulamin + polityka prywatności + powierzenie danych.** Prawnik wklejający opis sprawy przetwarza
   dane objęte tajemnicą zawodową. Potrzebna jasna informacja, co się dzieje z treścią pytań i jak długo
   jest trzymana (retencja 6 miesięcy już działa [ustalone z kodu: `RetentionService`]) oraz umowa
   powierzenia dla klientów biznesowych.
3. **Wyłączenie odpowiedzialności** — „wstępny research do weryfikacji, nie porada prawna". Dziś mówimy to
   testerom ustnie; w produkcie płatnym musi być w regulaminie i widoczne w interfejsie.
4. **AI Act** — trzy zadania już zidentyfikowane i ocenione jako tanie [`ANALIZA-AI-ACT.md` §1], w tym
   maszynowe oznaczanie treści generowanej. Domknąć przed publicznym udostępnieniem.

---

## 8. Proponowana kolejność

Bez wycen czasowych — z zależnościami i ryzykiem.

| # | Etap | Zależy od | Złożoność | Ryzyko zakopania się |
|---|---|---|---|---|
| 1 | Nazwa, domena, obietnica, cennik (decyzje, zero kodu) | — | mała | małe |
| 2 | Konta: Identity + GUID jako `UserId` + migracja (§3.2) + organizacja w schemacie (§3.3) | 1 (nazwa w mailach) | średnia | **średnie** — dotyka całej domeny |
| 3 | Plany i uprawnienia + przeniesienie liczników `CostGuard` do bazy | 2 | mała | małe |
| 4 | Landing + dopracowanie systemu UI (równolegle od początku) | 1 | średnia | małe |
| 5 | Stripe: Checkout + Portal + idempotentne webhooki | 3 | średnia | **średnie** — stany subskrypcji |
| 6 | Faktury / KSeF / VAT (równolegle od początku, poza kodem) | decyzja o podmiocie | ? | **wysokie** — poza naszą kontrolą |
| 7 | Regulamin, RODO, AI Act, model w UE | — (zacząć już) | średnia | **wysokie** — blokuje sprzedaż |
| 8 | Ops: Dockerfile, CI/CD, backupy, monitoring płatności | 5 | średnia | średnie |

Tani spike wart zrobienia przed etapem 5: **przejść całą ścieżkę płatniczą w trybie testowym Stripe na
jednym sztucznym planie**, zanim powstanie prawdziwy cennik. Rozstrzyga niewiadome o webhookach i stanach
za ułamek pracy pełnego wdrożenia.

---

## 9. Pytania blokujące

1. **Kto jest klientem**: prawnik indywidualny czy kancelaria z miejscami? (§3.3 — wpływa na schemat bazy)
2. **Historia alfy**: rozmowy testerów mają przetrwać przejście na konta? (§3.2)
3. **Model cenowy**: abonament ryczałtowy z limitem zapytań, czy darmowy poziom + płatny? Okres próbny —
   jest, i czy z kartą?
4. **Podmiot sprzedający i czy sprzedaż tylko w PL?** (§5.3)
5. **Nazwa** — zostajemy przy PrawoRAG czy szukamy nowej? (§6.1)

Odpowiedzi na 1 i 2 blokują etap 2 (schemat bazy). Pozostałe można rozstrzygać w trakcie.
