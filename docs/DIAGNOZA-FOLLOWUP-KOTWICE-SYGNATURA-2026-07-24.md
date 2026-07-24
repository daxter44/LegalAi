# Diagnoza: follow-up gubi trafiony akt — kotwice poprzedniej odpowiedzi udają sygnaturę orzeczenia

Data: 2026-07-24. Kontynuacja [DIAGNOZA-TOR-STRUKTURALNY-ART-1A.md](DIAGNOZA-TOR-STRUKTURALNY-ART-1A.md)
(ta diagnoza kończyła się „Etap 3 OK i etap 4 pokazuje dokładnie »1a« → problem gdzie indziej").
Znaleziony tu problem jest INNY niż ActHint (już naprawiony przez CIT-1) — dotyczy WYŁĄCZNIE pytań
z historią (follow-up), nie samego rozpoznania cytatu.

## Obserwacja użytkownika (punkt wyjścia)

Ta sama treść pytania, dwa różne wyniki w tej samej, aktualnie działającej wersji:

- Nowy wątek, pytanie wprost: „a Art. 1a USTAWA O PODATKACH I OPŁATACH LOKALNYCH ?" →
  **poprawnie**, ustawa `DU/1991/31` na pierwszym miejscu.
- Ten sam tekst jako DOPYTANIE po turze „jak po 1 stycznia 2025 r. kwalifikować obiekty do podatku
  od nieruchomości — budowla czy budynek?" → **źle**, same wyroki, zero ustawy.

To wykluczało `CitationParser`/`ActHint`/`ResolveActAsync`/`FetchArticleAsync` jako winowajcę same w
sobie (wszystkie 4 etapy zielone w izolacji, patrz diagnoza-matka) — różnica musiała siedzieć w
mechanizmie follow-upu.

## [FAKT — obalone] Hipoteza 1: sklejenie kontekstu psuje `CitationParser.Parse`

Zbudowany ręcznie tekst zgodny z `FollowUpQuery.Contextualize(history, question)` (Tura 1 + Tura 2 +
kotwice + fragment odpowiedzi), puszczony przez `CitationParser.Parse` bezpośrednio (bez DB) —
wynik: `art=1a hint=USTAWA O PODATKACH I OPŁATACH LOKALNYCH`, dokładnie poprawnie. Powód: bieżące
pytanie jest w `Contextualize` zawsze NA KOŃCU rdzenia (`baseCtx = poprzednie pytania + bieżące`),
więc „art. 1a" z Tury 2 jest i tak PIERWSZYM dopasowaniem `ArtRe` w całym tekście, niezależnie od
tego, co niosą kotwice/cytaty/fragment po nim. **Hipoteza odrzucona kodem, nie domysłem.**

## [FAKT — zmierzone na żywej bazie+TEI] Hipoteza 2: kotwice źródeł wyzwalają tor SYGNATUROWY

Odtworzona DOKŁADNIE logika `ChatService.AskAsync` (`src/PrawoRAG.Api/Services/ChatService.cs:41-49`)
przez tymczasową sondę (`src/PrawoRAG.Eval/_FollowUpProbe.cs`, `--probe-followup`) na żywo — DB
`192.168.100.11` + TEI. Dwa przebiegi: (A) świeże pytanie bez historii, (B) dokładna replikacja
2-przebiegowego retrievalu follow-upu (raw + kontekstowy, wybór przez `PickContextual`).

**PASS A (świeże pytanie)** — poprawnie, `structural` na wierzchu:

```
score=MaxValue  eli=DU/1991/31  art=1a  title=Ustawa … o podatkach i opłatach lok…
score=MaxValue  eli=DU/1991/31  art=1a  title=Ustawa … o podatkach i opłatach lok…
score=MaxValue  eli=DU/1991/31  art=1a  title=Ustawa … o podatkach i opłatach lok…
score=MaxValue  eli=DU/1991/31  art=1a  title=Ustawa … o podatkach i opłatach lok…
score=0,0164 sim=0,9017  eli=DU/1992/86  art=1a  (…)
```

**PASS B (follow-up, wynik WYBRANY przez `PickContextual` — kontekstowy wygrał, 0,9098 > 0,9017)**:

```
score=MaxValue  docType=judgment  eli=-  title=Wyrok WSA w Poznaniu, Wojewódzki Sąd Administracyjny w Pozna…
score=MaxValue  docType=judgment  eli=-  title=Wyrok WSA w Poznaniu, Wojewódzki Sąd Administracyjny w Pozna…
score=MaxValue  docType=judgment  eli=-  title=Wyrok WSA w Poznaniu, Wojewódzki Sąd Administracyjny w Pozna…
… (8/8 slotów TopK — ten sam wyrok, zero ustawy)
```

Wszystkie 8 slotów `TopK` zajęte chunkami JEDNEGO wyroku, ze `Score = double.MaxValue` — to sygnatura
tego samego poziomu co dokładny cytat aktu, nie wynik semantyczny. Odtwarza dosłownie objaw
użytkownika: „same wyroki, zero ustawy".

## Mechanizm (potwierdzony w kodzie)

1. `FollowUpQuery.Contextualize(history, question)` (`src/PrawoRAG.Domain/Retrieval/FollowUpQuery.cs:67-84`)
   dokleja do sklejonego tekstu **kotwice źródeł poprzedniej odpowiedzi** (`SourceAnchors`) —
   zaprojektowane pod anaforę („kim jest osoba z POWYŻSZEJ ODPOWIEDZI?"). W tym przypadku Tura 1
   cytowała orzeczenia, więc kotwica brzmi dosłownie: `"[2] Wojewódzki Sąd Administracyjny w
   Poznaniu, I SA/Po 594/17"`.
2. To wygląda jak sygnatura akt. `CaseNumberKey.Detect(query.Text)`
   (używane przez `HybridRetriever.SignatureAsync`, `HybridRetriever.cs:306-308`) łapie ją —
   sklejony tekst follow-upu przypadkiem wygląda jak pytanie o KONKRETNE orzeczenie z Tury 1,
   mimo że użytkownik o nie nie pytał.
3. W składaniu wyniku (`HybridRetriever.cs:204`) `signature` jest PIERWSZY:
   `signature.Concat(actReference).Concat(structural).Concat(bridge).Concat(ranked).Take(TopK)`.
   Tor sygnaturowy nie ma cap-u dzielonego z resztą torów — złapana sygnatura ciągnie WSZYSTKIE
   swoje chunki ze `Score = double.MaxValue`. Przy `TopK=8` to wypełnia CAŁY budżet, zanim
   `structural` (który — potwierdzone PASS A i hipotezą 1 — poprawnie rozwiązuje `art. 1a` →
   `DU/1991/31`) dostanie choćby jeden slot.
4. `MaxSimilarity`, na którym stoi `PickContextual` (`RetrievalResult.MaxSimilarity`,
   `Retrieval.cs:69-79`), liczy się WYŁĄCZNIE z toru gęstego — nie widzi w ogóle, że `signature`
   ukradł sloty. Kontekstowy wariant wygrywa wybór (0,9098 > 0,9017 + margines), więc do promptu
   idzie WŁAŚNIE ten zdominowany przez jeden wyrok wynik.

## Wniosek

To NIE jest luka w rozpoznawaniu cytatów (ta była naprawiona przez CIT-1) i NIE jest awaria
`ResolveActAsync`/`FetchArticleAsync` (oba zielone, zweryfikowane bezpośrednio na bazie). To
INTERAKCJA dwóch niezależnie poprawnych mechanizmów: anchor-folding (pod anaforę, zamierzone
wzbogacenie SEMANTYCZNE) ubocznie zasila tor EXACT-MATCH (`CaseNumberKey`/`SignatureAsync`), który
nie ma tolerancji ani cap-u dzielonego z resztą torów strukturalnych.

Dotyczy SYSTEMOWO każdego follow-upu, w którym poprzednia odpowiedź cytowała orzeczenie po
sygnaturze — nie jest to specyficzne dla u.p.o.l. ani dla tego pytania.

## Otwarte — kierunek naprawy nierozstrzygnięty

Dwie niezależne opcje, nie oceniam która trafniejsza bez dodatkowych danych:

- (a) Kotwice/cytaty z `Contextualize` nie powinny zasilać `CaseNumberKey.Detect`/`CitationParser`
  w ogóle — tylko tor gęsty/BM25 (do tego były projektowane). Wymagałoby oddzielenia „tekstu do
  embeddingu" od „tekstu do torów exact-match" w `RetrievalQuery`.
- (b) `SignatureAsync` (i analogicznie `ActReferenceAsync`/`StructuralAsync`) potrzebuje własnego
  mniejszego cap-u NIEZALEŻNEGO od reszty budżetu `TopK`, żeby jeden złapany tor nie mógł zjeść
  100% slotów kosztem pozostałych torów exact-match.

## Narzędzie

Tymczasowa sonda `src/PrawoRAG.Eval/_FollowUpProbe.cs` + flaga `--probe-followup` w `Program.cs`
(niescommitowane celowo — do decyzji, czy zostaje jako trwałe narzędzie diagnostyczne, analogicznie
do `--probe-chunk`, czy to jednorazowy skrypt do usunięcia po naprawie). Odtwarza 1:1 logikę
`ChatService.AskAsync`, więc nadaje się też do weryfikacji wybranego fixu bez uruchamiania całego API.
