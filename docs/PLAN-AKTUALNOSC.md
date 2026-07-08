# Plan: aktualność prawa — nowelizacje niewchłonięte do tekstu jednolitego

Metodyczne domknięcie luki: opierając się na tekstach jednolitych (t.j.), korpus jest aktualny „do
ostatniej konsolidacji", nie „do ostatniej nowelizacji". Zamiast tylko ostrzegać — **aktywnie dołączamy
świeże nowele do kontekstu**, a LLM zestawia stan po zmianie. Do przeglądu przed implementacją.

## Problem (trzecia klasa błędu)

Cykl zmiany prawa: `ogłoszenie noweli → (vacatio) → wejście w życie → (tygodnie…18 mies.) → nowy t.j.`
Między wejściem w życie a nowym t.j. przepis **obowiązuje, ale nie ma go w żadnym tekście jednolitym**.
Odpowiedź z takiego t.j. jest **wierna źródłu, poprawnie zacytowana i pewna** → żadna istniejąca bramka
(citationCheck, abstynencja) jej nie łapie. Naprawa możliwa TYLKO przez jawność temporalną + dołączenie noweli.

## Fakty empiryczne (api.sejm.gov.pl, zweryfikowane)

- KPC: najnowszy t.j. `DU/2026/468` (ogł. 2026-04-07); nowele `DU/2026/473`, `DU/2026/830` (weszły później)
  NIE są w nim; `DU/2025/1172` (weszła 1.03.2026) — JEST (ogłoszona w 2025, przed konsolidacją).
- Nowele mają `type=Ustawa, status=obowiązujący` → **discovery je łapie** (są w korpusie jako dokumenty-akty).
- `references."Akty zmieniające"` daje `{id, date}` (date = data wejścia w życie — do etykiety „obowiązuje od…").
- **Test wchłonięcia (czysty, bez zapytań): (rok, poz.) noweli > (rok, poz.) t.j. ⇒ niewchłonięta.**

## Architektura

Krok **po retrievalu, przed budową promptu** (`TemporalAugmenter`): dla każdego zwróconego źródła-aktu
sprawdź, czy ma nowele niewchłonięte (test dat); jeśli tak — znajdź w korpusie fragmenty tych nowel
dotyczące cytowanych artykułów i **dołącz jako dodatkowe źródła**; LLM instruowany, by przedstawić stan
PO zmianie, cytując oba. Świeżość korpusu utrzymuje **codzienny delta-sync ELI**.

## Decyzje projektowe (z ustaleń)

- **D1** Porównanie po **ogłoszeniu**, nie wejściu w życie (t.j. wchłania ogłoszone przed odcięciem, nawet
  z przyszłym vacatio) → proxy = porównanie kluczy ELI (rok, poz.). Data wejścia = tylko etykieta.
- **D2** Dołączamy **tylko trafione fragmenty** noweli (dopasowanie po numerze cytowanego artykułu), nie
  całą nowelę (zmienia wiele ustaw/dziesiątki art. — rozsadziłaby kontekst).
- **D3** Delta-sync: raz dziennie listing bieżącego rocznika DU (1–2 zapytania), nie 14k. Nowa Ustawa/Rozp.
  → fetch; nowe Obwieszczenie (t.j.) → re-fetch aktu bazowego. Tygodniowy pełny przegląd jako siatka.
- **D4** LLM **zestawia** (stary tekst + nowela), NIE „cicho przepisuje" przepisu — musi cytować oba źródła;
  błąd składania wykrywalny przez prawnika (oba teksty obok). Zero własnej konsolidacji.
- **D5** Fallback: gdy nowele istnieją, ale żaden fragment nie pasuje do cytowanych art. → kompaktowy baner
  „stan na [data t.j.]; po tej dacie ogłoszono: DU/…" (siatka bezpieczeństwa, zamiast milczenia).

## Zadania per epik

### AKT-0 — Utrwalenie tożsamości konsolidacji + nowel (fundament, L1)
- [AKT-0.1] `EliSejmConnector`: przy fetchu utrwal `consolidatedTextId` (adres najnowszego t.j., np. DU/2026/468)
  do metadanych — dziś rozwiązywany, ale nietrwały.
- [AKT-0.2] `ActNormalizer` → `TypedMetadata`: `consolidatedTextId` + `amendments` (lista {eliId, effectiveDate}
  z `references."Akty zmieniające"` obecnych w SourcePayload aktu bazowego). *Kryt.: akt KPC w bazie niesie t.j. + listę nowel.*
- [AKT-0.3] Migracja/backfill nie potrzebna dla nowych; istniejące uzupełni re-`process` (bez re-embeddingu — to metadane).

### AKT-1 — Logika wchłonięcia (czysta, testowalna; D1)
- [AKT-1.1] `Consolidation.IsUnabsorbed(amendmentEli, tjEli)` (Domain): parsuje (rok, poz.), zwraca true gdy nowela > t.j.
- [AKT-1.2] Testy: KPC (1172 wchłonięta, 473/830 nie), różne roczniki, braki/nietypowe id. *Kryt.: zgodne z danymi empirycznymi.*

### AKT-2 — TemporalAugmenter (rdzeń; D2/D5)
- [AKT-2.1] Serwis: wejście = zwrócone chunki + cytaty (z `CitationParser`); dla każdego źródła-aktu odczytaj
  `amendments`, odfiltruj niewchłonięte (AKT-1), dla każdej znajdź w korpusie chunki (dokument = eliId noweli)
  zawierające numer cytowanego artykułu (text match), dołącz jako źródła z etykietą „nowelizacja, obowiązuje od [date]".
- [AKT-2.2] Fallback (D5): nowele niewchłonięte są, ale brak dopasowanego fragmentu → dołącz kompaktowy komunikat-źródło.
- [AKT-2.3] Wpięcie w `ChatService` (i `/api/chat`) po retrievalu, przed `GroundedPrompt.Build`. *Kryt.: pytanie o art. z niewchłoniętą nowelą → nowela w źródłach.*

### AKT-3 — Instrukcja LLM (D4)
- [AKT-3.1] `GroundedPrompt.SystemPrompt`: reguła — „jeśli wśród źródeł jest NOWELIZACJA zmieniająca cytowany
  przepis, przedstaw stan PO zmianie, wskaż co i od kiedy, cytuj oba źródła; nie przepisuj po cichu". *Kryt.: odpowiedź zestawia stary + zmianę z cytatami.*

### AKT-4 — Jawność temporalna w UI (L1 widoczne)
- [AKT-4.1] Źródło-akt pokazuje „stan prawny na: [data t.j.]"; nowela — chip „obowiązuje od [date]".
- [AKT-4.2] Baner nieaktualności (fallback D5) widoczny przy odpowiedzi. *Kryt.: prawnik zawsze widzi datę stanu prawnego.*

### AKT-5 — Codzienny delta-sync ELI (D3, świeżość)
- [AKT-5.1] ✅ Tryb `sync-eli`: discovery bieżącego rocznika (+`Eli:Sync:YearsBack`); RawFetchRunner
  pomija akty już w magazynie → pobiera TYLKO nowe pozycje (nowe ustawy/rozporządzenia, w tym nowelizacje),
  potem process. **Delta = skip-existing** (nie trzeba kursora). Scheduler = zewnętrzny cron (jak SAOS).
- [AKT-5.2] ✅ **Relink w stanie ustalonym:** świeżo pobrana nowela nie odświeżała listy `unabsorbedAmendments`
  aktu BAZOWEGO — fetch pomija akt bazowy (skip-existing), a `process` pomija niezmienioną treść (`ContentHash`
  bez zmian → `Skipped`; lista żyje w `SourcePayload.references`, odsprzężona od hasha). Rozwiązanie (opcja A —
  lekki relink metadanych): na końcu `sync-eli` `AmendmentRelinkRunner` dobiera SAME metadane aktów bazowych
  (JSON `acts/{addr}`, bez text.html/PDF — tani ruch), przelicza listę współdzieloną logiką
  `EliSejmConnector.ExtractUnabsorbedAmendments` i — gdy się zmieniła — patchuje TYLKO klucze
  `unabsorbedAmendments`/`consolidatedTextId` w metadanych (bez re-embeddingu, bez ruszania idempotencji
  fetchu/process). Wyłącznik: `Eli:Sync:Relink=false`.
  **Świadomy trade-off:** raw-store na dysku zostaje nieświeży → pełny offline `process` (rebuild z surowych)
  odtworzyłby listę ze starego payloadu i cofnął linki **do następnego dziennego `sync-eli`** (samonaprawcze,
  okno ≤1 dzień). Odświeżanie payloadu w raw-store poza zakresem.

### AKT-6 — Pomiar (E5)
- [AKT-6.1] ✅ Golden set: kategoria `Freshness` — pytanie o przepis z niewchłoniętą nowelą. Harness Eval
  wpina `TemporalAugmenter` po retrievalu (parytet z `/api/chat`) i scoruje **obiektywne**: metryka
  `FreshnessRecall` = czy oczekiwana nowela (`ExpectedAmendmentEli`) jest w źródłach po augmentacji. „Poprawne
  zestawienie" stary↔nowy stan jest niuansowe → `needsLawyer` (nie scorujemy automatycznie). Strażnik regresji
  augmentera (AKT-2) i świeżości (AKT-5): mierzone przed/po. Wpis `fresh-kpc-nowela` (KPC + nowela po t.j.) —
  pytany artykuł i dokładny ELI noweli do potwierdzenia na M4 (runbook Krok 3/4), potem korekta `expected*`.

## Kolejność

**AKT-0 → AKT-1 → AKT-2 → AKT-3 → AKT-4** (rdzeń: wykrycie + dołączenie + LLM + jawność) **→ AKT-5** (świeżość) **→ AKT-6** (pomiar).

## Zasada bezpieczeństwa

Nowela tylko **DOKŁADA** źródła; brak nowel / brak dopasowania → zachowanie jak dziś. LLM **zestawia** (nie
konsoliduje) — oba źródła cytowane i widoczne. Zero własnego nakładania diffów na tekst (praca redakcji Sejmu).

## Zależności

- Wymaga korpusu z **discovery ON** (nowele obecne jako dokumenty) — łączy się z budową pełnego korpusu v1 na M4.
- Dopasowanie „art. X w treści noweli" korzysta z `CitationParser` (QU-0). Integracja weryfikowalna na M4 (DB).
