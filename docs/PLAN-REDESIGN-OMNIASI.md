# Plan wdrożenia redesignu OmniaSI (RED)

> **STAN NA 2026-08-31 (koniec sesji): fazy 0–4 WDROŻONE** (commity `cdb9a20` fazy 0–4,
> `4c96628` poprawki po przeklikaniu). Testy 889/889, smoke na żywym procesie przeszedł,
> właściciel przeklikał i zaakceptował z trzema poprawkami (wdrożone — sekcja „Poprawki po
> przeklikaniu" niżej). Makiety źródłowe: `design/*.dc.html` + artefakt „OmniaSI — makiety UI".
>
> **ZOSTAŁO (faza 5 + drobne):**
> - RED-5.1: przejście checklisty inwariantów na PEŁNYM środowisku (M4: baza + TEI + LLM) —
>   zwłaszcza pełna tura czatu z klikaniem [n], analiza end-to-end, telefon;
> - (+) licznik zużycia X/Y w stopce sidebara i na /konto (wymaga zapytania o usage_counters);
> - RED-4.4: dedykowany retusz /o-systemie i /dokument (dziedziczą tokeny, bez własnej przejrzałki);
> - hero-CTA landingu w trybie kont a11y-sprawdzić po włączeniu Auth:Enabled (dziś testowane w alfie);
> - follow-up DO DECYZJI (zmiana zachowania, poza RED): przełączanie rozmów w trakcie generowania —
>   sekcja na końcu dokumentu.
>
> Decyzje podjęte w trakcie: Szukaj bez pozycji w nawigacji (funkcja do dopracowania, trasa
> zostaje); zmiana hasła = link do istniejącego /haslo/reset (osobnej funkcji nie ma i nie udajemy);
> panel źródeł kontekstowy per tura (numeracja [n] jest per tura) ze zwijaniem na desktopie.

Data: 2026-08-31. Źródła prawdy: **wygląd = makiety** (`design/*.dc.html`, artefakt „OmniaSI — makiety
UI"), **zachowanie = obecny kod**. Zasada nienegocjowalna: redesign nie może odebrać ŻADNEJ
funkcjonalności ani zmienić semantyki działania — każda faza kończy się buildem, kompletem testów
(889) i przejściem checklisty inwariantów danego ekranu (sekcja na końcu).

Konwencja: zadanie oznaczone „(+)" jest ADDYTYWNE — dodaje prezentację danych, które system już ma
(dozwolone, bo niczego nie odbiera); „(?)" wymaga decyzji właściciela przed realizacją.

## Faza 0 — fundamenty (bez zmian w markup)

- **RED-0.1** Fonty self-hosted: Inter 400/500/600/700 (woff2, **latin + latin-ext** — polskie znaki)
  do `wwwroot/fonts/` + `@font-face`; Palatino jako systemowy stack display (bez hostowania).
  ŻADNYCH zewnętrznych hostów — CSP `font-src 'self'` zostaje bez zmian (suwerenność).
- **RED-0.2** `tokens.css` 2.0: wartości wprost ze `styles/styleguide.html` (kolory sl-*, gradient
  135° #2563EB→#7C3AED, hero-deep, promienie 4/8/12/16, cienie xs–lg/card/lift/accent, focus-ring,
  tranzycje, skala typograficzna). Stare nazwy zmiennych (`--c-*`, `--s-*`, `--radius*`, `--shadow`,
  `--focus`) zachowane jako ALIASY nowych wartości — istniejące reguły w app.css i style per-component
  nie mogą się wywrócić w połowie prac.
- **RED-0.3** `app.css`: komponenty bazowe wg makiet — przyciski (primary gradient+shadow-accent,
  secondary, ghost, danger), pola (bg #F3F5F9, border 1.5px, focus-ring), karty (radius 16, shadow-card),
  badge/pigułki, banery (info/warn/danger), tabele. Klasy istniejące (`.btn`, `.banner`, `.badge`,
  `.source-card`, `.msg`, …) restylowane POD TYMI SAMYMI NAZWAMI — markup jeszcze nietknięty.

## Faza 1 — rebranding

- **RED-1.1** Nazwa OmniaSI wszędzie w UI: MainLayout (logo = gradientowy kwadrat + wordmark Palatino),
  `<title>` w App.razor, nagłówki AuthPages/BillingPages, stopki e-maili? (sprawdzić szablony Resend),
  strona /o-systemie. Namespace'y i nazwy projektów **bez zmian** (PrawoRAG zostaje w kodzie).
- **RED-1.2** Favicon (gradientowy kwadrat) do wwwroot + link w App.razor.

## Faza 2 — czat (największa)

- **RED-2.1** Sidebar: przełącznik Czat|Analiza pod logo (zakładka Analiza widoczna TYLKO przy
  `Analysis:Enabled` — dokładnie jak dziś w MainLayout); lista rozmów (LoadConversation, active,
  disabled przy _busy, tytuł+data — bez zmian logiki); „Nowa rozmowa"; stopka użytkownika
  (DisplayName + chevron → /konto tylko przy `Billing:Enabled`, jak dziś w MainLayout). (+) plan
  i licznik zapytań w stopce (dane z IEntitlements/usage — tylko odczyt).
- **RED-2.2** Stan pusty: nagłówek „W czym mogę pomóc?", duże pole, chipy przykładowych pytań
  (klik = wstawienie do inputu, NIE automatyczna wysyłka), link do /o-systemie zostaje.
- **RED-2.3** Panel źródeł kontekstowy (desktop ≥1200px): nowy stan `_activeExchangeId`
  (domyślnie ostatnia/generująca tura); pod każdą odpowiedzią kotwica „Źródła (n)" przełączająca
  panel; nagłówek panelu ZAWSZE nazywa turę (skrót pytania). Do panelu przenoszą się WSZYSTKIE
  dzisiejsze elementy sekcji źródeł, bez ubytku: grupowanie po dokumencie (plan SAS) z licznikiem
  fragmentów i badge „+N z sąsiedztwa", tag „kontekst" przy source-neighbour, badge „🕓 nowelizacja —
  obowiązuje od …", link „oryginał ↗", snippet, `legal-bases` (chips w details). Kotwice
  `#src-{anchor}-{n}` i numeracja [n] NIETKNIĘTE.
- **RED-2.4** cite.js: klik chipa [n] w odpowiedzi → (a) przełącza panel na turę chipa,
  (b) scroll+highlight źródła n w panelu; na mobile — dzisiejsze zachowanie (scroll do inline).
- **RED-2.5** Mobile/wąski ekran (<1200px): panel źródeł DEGRADUJE do dzisiejszych inline
  `<details>` pod odpowiedzią (zero utraty; wysuwany arkusz — po deployu). Toggle ☰ historia zostaje.
- **RED-2.6** Restyle elementów tury — wszystkie zachowane 1:1 funkcjonalnie: pastylka etapów pracy
  (label+licznik+sekundy, spinner w trakcie), akordeon „Rozumowanie modelu" (open przy streamingu),
  retry-note drugiej rundy, banner „nie przeglądałem bazy" (router), verify-banner z badge'ami
  (✓ cytaty zgodne / ⚠ do sprawdzenia / ↻ poprawiona / tokeny za flagą Diagnostics), banner odmowy,
  banner błędu, feedback (👍 / zła odpowiedź / niepotrzebna odmowa / podziękowanie), sekcja
  „Twój dokument [D…]" przy załączniku.
- **RED-2.7** Pas wejścia: pole + przycisk gradient; załącznik PDF (attach-btn, chip przetwarzania,
  usuń ✕, notice) przy `Documents:Enabled`; ostrzeżenie PII (SensitiveDataDetector) nad polem;
  komunikaty limitów (RateGuard/CostGuard) bez zmian treści; disclaimer pod polem.

## Faza 3 — analiza dokumentów

- **RED-3.1** Sidebar „Moje analizy": toggle Czat|Analiza, lista raportów ze status-ikonami
  (Done/Analyzing/Interrupted/Failed — ikony zamiast dzisiejszych emoji StatusIcon), Nowa analiza,
  stopka jak w czacie.
- **RED-3.2** Setup: upload PDF (InputFile accept=.pdf, chip przetwarzania, usuń), textarea intencji
  z placeholderem jak dziś, PII-warn, submit disabled wg dzisiejszych warunków.
- **RED-3.3** Raport: nagłówek pliku (nazwa, strony, liczba fragmentów, id sesji z titlem) +
  (+) zbiorcze chipy werdyktów (N OK / N RYZYKO / N BRAK ŹRÓDEŁ — policzone z istniejących danych);
  karta Streszczenia z dzisiejszym zastrzeżeniem; jednostki jak dziś: `<details>` w kolejności
  dokumentu, open przy Risk/Error, badge werdyktu (OK/RYZYKO/BRAK ŹRÓDEŁ/BŁĄD/„w kolejce"/
  „nieprzeanalizowany" w trybie degraded), blockquote fragmentu (trunc 400), odpowiedź markdown
  z kotwicami, feedback per jednostka (tylko gdy wiersz w DB — jak dziś), źródła inline per jednostka
  (details, karty jak w czacie), przycisk „↻ ponów" przy błędzie.
- **RED-3.4** Bannery przebiegu: postęp/Anuluj w trakcie, „Ponów nieudane (n)", Failed, Interrupted,
  degraded-notice, UnitsTruncated — wszystkie zachowane.
- **RED-3.5** Dopytania (SPK-6): wymiany user/assistant (abstain banner, streaming „…", źródła open,
  błąd), pole dopytania z dzisiejszym placeholderem, disabled przy _followUpBusy.

## Faza 4 — pozostałe ekrany

- **RED-4.1** AuthPages.cs: wspólny szkielet `Page()` na split-layout z makiety Logowanie (lewa
  połowa gradient z obietnicą, prawa karta formularza); wszystkie strony flow: logowanie (z `powrot`),
  rejestracja, potwierdź e-mail (+ ponów), reset hasła, nowe hasło, wylogowanie (potwierdzenie POST).
  Antiforgery tokens, komunikaty błędów i przekierowania NIETKNIĘTE.
- **RED-4.2** BillingPages /konto wg makiety: karta Plan (badge planu i statusu, ważność
  `PlanValidUntilUtc`, guziki Wykup/Zmień plan + Zarządzaj płatnościami wg `hasSubscription` — jak
  dziś), (+) pasek zużycia X/Y z liczników, karta Dane konta (e-mail + status potwierdzenia z
  Identity; „hasło" linkuje do ISTNIEJĄCEGO /haslo/reset — zmiany hasła w produkcie NIE MA i nie
  dodajemy), karta Prywatność (teksty jak w makiecie) + Wyloguj (istniejący flow /wylogowanie).
- **RED-4.3 (?)** Szukaj.razor: makiet nie ma. Decyzja: (a) trzecia zakładka toggle'a
  Czat|Analiza|Szukaj + restyle w systemie, albo (b) zostaje tylko pod /szukaj bez zakładki.
  Do rozstrzygnięcia przed Fazą 2 (wpływa na toggle).
- **RED-4.4** OSystemie.razor + Dokument.razor: restyle na nowych tokenach (typografia, karty) —
  treść bez zmian.
- **RED-4.5** MainLayout: ekrany z własnym sidebarem (czat/analiza) nie pokazują globalnego headera;
  pozostałe strony (konto, o-systemie, szukaj, dokument) dostają topbar z makiety Konto (logo,
  Czat · Analiza[flaga] · [Szukaj?], aktywna pozycja, user+wyloguj). Linki i warunki flag DOKŁADNIE
  dzisiejsze (Analysis.Enabled, Billing.Enabled, Auth.Enabled → /wylogowanie vs /wyjscie).
- **RED-4.6** Landing „/": nowa strona anonimowa wg makiety (hero, bento wyróżników z porównaniem
  [PLACEHOLDERY], sekcja analizy, tabela porównawcza, cennik 0/[CENA], stopka). Renderowanie
  STATYCZNE (bez obwodu SignalR — decyzja US-2.4). Zalogowany wchodzący na „/" NADAL trafia
  do /czat (dzisiejszy redirect zostaje); anonim widzi landing zamiast dzisiejszego przekierowania.
  CTA respektują flagi: przy Auth:Enabled=false (alfa) przyciski prowadzą do bramki kodów jak dziś.
- **RED-4.7** Placeholder strony /regulamin i /polityka-prywatnosci (statyczne, „treść w
  przygotowaniu") — linkowane z landingu/stopek; wersjonowanie zgód to osobny task US-2.10, poza RED.

## Faza 5 — domknięcie

- **RED-5.1** Przejście checklisty inwariantów (niżej) ekran po ekranie na działającej aplikacji.
- **RED-5.2** Responsywność: telefon dla czatu/analizy/auth/konta (US-2.7): sidebar chowany (jest),
  panel źródeł → inline (RED-2.5), formularze pełna szerokość; komunikat zerwanego łącza Blazora —
  ostylować szablon reconnect zamiast surowego.
- **RED-5.3** Build + 889 testów po KAŻDEJ fazie; commit per faza (format hooka).

## Checklista inwariantów (nic z tego nie może zniknąć)

**Czat:** historia rozmów (lista/aktywna/ładowanie/Nowa) · stan pusty z linkiem do /o-systemie ·
pytanie+wysyłka (Enter) · etapy pracy z licznikiem sekund · rozumowanie na żywo (akordeon) ·
odpowiedź markdown z klikalnymi [n] · kotwice #src-… · źródła: grupowanie po dokumencie, sąsiedztwo
(„kontekst", „+N z sąsiedztwa"), badge nowelizacji z datą, link oryginał, snippet, podstawy prawne ·
sekcja [D…] załącznika · odmowa (banner) · „nie przeglądałem bazy" (router) · retry-note drugiej
rundy · verify-banner: ✓/⚠/↻ + tokeny za flagą · feedback 3 opcje + podziękowanie · błąd tury ·
załącznik PDF za flagą (dodaj/chip/usuń/notice) · PII-warn · limity RateGuard/CostGuard · mobile ☰.

**Analiza:** setup (PDF, intencja, PII, walidacja submitu) · lista moich analiz ze statusami ·
id sesji (przeżywa odświeżenie) · postęp + Anuluj · streszczenie z zastrzeżeniem · jednostki w
kolejności dokumentu, pełna lista, open przy ryzyku/błędzie · werdykty OK/RYZYKO/BRAK ŹRÓDEŁ/BŁĄD/
w kolejce/nieprzeanalizowany · blockquote fragmentu · feedback per jednostka (warunkowy) · źródła
per jednostka · ↻ ponów (jednostka i zbiorczo) · bannery Failed/Interrupted/Truncated/degraded ·
dopytania z abstencją, streamingiem, źródłami, błędem.

**Auth:** rejestracja→potwierdzenie→logowanie(powrot)→reset→nowe hasło→wylogowanie(POST+token) ·
komunikaty błędów walidacji · antiforgery wszędzie.

**Konto:** plan/status/ważność · Checkout i Portal (formularze POST z tokenem) · warunek
hasSubscription · zero logiki uprawnień w stronie.

**Globalne:** flagi Auth/Billing/Analysis/Documents/Diagnostics sterują widocznością DOKŁADNIE jak
dziś · CSP bez nowych hostów (fonty self-hosted) · /wyjscie (alfa) vs /wylogowanie · bramka kodów
zaproszeń nienaruszona · redirect / → /czat dla zalogowanych.

## Otwarte decyzje przed startem

1. **RED-4.3**: Szukaj jako trzecia zakładka czy bez zakładki?
2. Kolejność realizacji proponowana: 0 → 1 → 2 → 3 → 4.1/4.2 → 4.5 → 4.6/4.7 → 5.

## Poprawki po przeklikaniu właściciela (2026-08-31, wdrożone)

1. Panel źródeł zwijany na desktopie (✕ zwija, boczna szyna „Źródła" przywraca; kotwica pod
   odpowiedzią i klik [n] też rozwijają).
2. Fix: panel świecił pustką przy >1 wiadomości — kotwica wskazująca turę bez źródeł spada teraz
   ZAWSZE na najnowszą turę ze źródłami.
3. Historia rozmów: spinner przy rozmowie generującej odpowiedź + ✓ przez 5 s po zakończeniu;
   zablokowane pozycje z tytułem „Poczekaj na zakończenie…".

## Follow-up do decyzji: przełączanie rozmów W TRAKCIE generowania

Dziś strumień odpowiedzi żyje w stanie widoku czatu — przełączenie rozmowy w trakcie by go ubiło,
więc historia jest zablokowana do końca tury (jak przed redesignem; teraz z czytelnym spinnerem).
Odblokowanie = generowanie w tle per rozmowa (wzorzec AnalysisSessionStore: sesja poza obwodem,
widok tylko podgląda). To zmiana ZACHOWANIA, nie designu — poza kontraktem RED; do osobnej decyzji.
