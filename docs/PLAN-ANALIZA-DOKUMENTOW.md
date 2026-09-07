# Plan: Analiza dokumentów (DOC) — PDF jako załącznik do czatu

Data: 2026-07-18. Decyzje wejściowe (potwierdzone):
- **v1 = wariant A**: pytanie + PDF w istniejącym czacie. Dokument dostarcza FAKTY, korpus dostarcza PRAWO.
- **Przepływ danych jak reszta systemu** (PL/UE, obecnie lokalny Bielik) — bez osobnego ograniczenia
  self-hosted i bez modułu anonimizacji w v1 (kierunek #2 z planu KAZ zostaje osobnym klockiem).
- **v2 (po implementacji KAZ)**: ten sam fundament (ekstrakcja + doc-retrieval) jako wejście trybu kazusu.

## Cel i granice

Prawnik dołącza PDF (umowa, pismo procesowe, wyrok) do pytania w czacie: *„czy kara umowna z §7 tej
umowy jest zgodna z prawem?"*. System wybiera trafne fragmenty dokumentu, dokłada prawo z korpusu
i odpowiada z DWIEMA przestrzeniami cytowań: `[D1]`… (Twój dokument) i `[1]`… (przepisy/orzecznictwo).

## Decyzje projektowe (z uzasadnieniem)

1. **Dokument NIGDY nie trafia do korpusu ani do bazy.** Pisma klientów = tajemnica zawodowa (kluczowy
   argument zaufania produktu). Życie dokumentu = pamięć obwodu Blazora (upload → analiza → GC); zero
   zapisu na dysk/do Postgresa. Historia rozmowy zapisuje wyłącznie metadane („załącznik: umowa.pdf,
   12 stron") — po odświeżeniu strony dokument trzeba dołączyć ponownie (komunikowane w UI).
   Konsekwencja: zero migracji, zero retencji, zero czyszczenia.
2. **Doc-retrieval in-memory, nie w pgvector.** ~100 chunków z 50 stron → `EmbedPassagesAsync` (TEI,
   sekundy) → cosine do pytania liczony w pamięci → top-K fragmentów. Zero nowej infrastruktury;
   rozwiązuje też okno kontekstu (Bielik serwowany z 4096 tok. — cały PDF i tak się nie mieści,
   selekcja jest konieczna niezależnie od modelu).
3. **Dwie przestrzenie cytowań: `[Dk]` ≠ `[n]`.** Prompt dostaje sekcję `DOKUMENT` (fragmenty [D1..])
   nad istniejącymi PRZEPISY/ORZECZNICTWO ([1..]); zasada systemowa: fakty z DOKUMENTU, prawo ze
   źródeł — dokumentu nie wolno cytować jako podstawy prawnej. CitationValidator i klikalne linki
   rozróżniają obie przestrzenie — bez tego odpowiedź mieszałaby twierdzenia klienta z normami.
4. **Uczciwa odmowa dla skanów.** PdfPig nie robi OCR; bramka jakości (< ~200 znaków/stronę →
   „ten PDF wygląda na skan — obsługujemy dokumenty z warstwą tekstową"). Filozofia jak przy
   abstynencji: odmowa zamiast udawania.
5. **Abstynencja bez zmian** — bramka liczy się z retrievalu KORPUSU. Dokument nie podbija sygnału:
   nie może zamienić „nie znam prawa w tym temacie" w odpowiedź (fakty bez prawa = odmowa jak dotąd).
6. **Limity:** plik ≤ 10 MB / ≤ 100 stron; 1 dokument na rozmowę (v1); tokeny fragmentów dokumentu
   liczą się do CostGuard jak reszta promptu (Record już sumuje znaki). Ekstrakcja i embedding
   w try/catch z timeoutem — uszkodzony/złośliwy PDF nie może zabić obwodu.
7. **Follow-upy działają w ramach żywego obwodu**: dokument (chunki+embeddingi) trzymany per
   rozmowa w pamięci komponentu; każde kolejne pytanie robi świeży doc-retrieval po SWOIM tekście.

## Przepływ (v1)

```
InputFile (PDF ≤10 MB)
   │  PdfTextExtractor (PdfPig): tekst per strona + bramka jakości (skan → komunikat, stop)
   ▼
chunking (reużycie IChunker z ingestii) → EmbedPassagesAsync (TEI) → DocumentContext w pamięci
   │
pytanie ─► EmbedQueryAsync ─► cosine top-K_doc (in-memory)          ─┐
   │                                                                 │
   └─► retrieval korpusu jak dotąd (hybryda + most cytowań + AKT)  ─┤
                                                                     ▼
GroundedPrompt.Build: sekcja DOKUMENT [D1..] + PRZEPISY/ORZECZNICTWO [1..]
   ▼
streaming → CitationValidator (obie przestrzenie) → UI: karty [D] „Twój dokument” + karty [n] „Źródła”
```

## Taski implementacyjne

### DOC-0: `PdfTextExtractor` (PrawoRAG.Api/Services; pakiet PdfPig jak w Ingestion)
- `Extract(Stream, maxPages)` → `PdfText(Pages: IReadOnlyList<string>, IsScanLike: bool)`;
  bramka jakości: średnio < 200 znaków/stronę → `IsScanLike=true`.
- Twarde limity: rozmiar, liczba stron, timeout; wyjątki PdfPig → czytelny błąd, nie 500.
- Testy: fixture PDF born-digital (tekst wychodzi), fixture „pusty/skan” (IsScanLike), za duży → odcięty.

### DOC-1: `DocumentContext` + doc-retrieval in-memory (PrawoRAG.Api/Services)
- `DocumentContext { FileName, PageCount, Chunks: [(Text, Embedding)] }` — budowany raz per upload
  (chunking istniejącym `IChunker`, embeddingi `EmbedPassagesAsync`).
- `SelectFragments(questionEmbedding, topK)` → top-K chunków po cosine (czysta funkcja, testowalna
  z FakeEmbeddingProvider). K_doc domyślnie 4 (budżet promptu obok 8 źródeł korpusu — do pomiaru).
- Testy: selekcja po podobieństwie, stabilna kolejność, pusty dokument → pusto.

### DOC-2: `GroundedPrompt` — sekcja DOKUMENT + przestrzeń [D] + WARUNKOWY system prompt
- `Build(question, chunks, history, docFragments)` (overload; brak fragmentów = dzisiejsze zachowanie,
  zero regresji): sekcja `DOKUMENT (fragmenty załącznika użytkownika):` z [D1..] NAD źródłami.
- **System prompt modyfikowany TYLKO gdy załącznik obecny** — bez dokumentu `SystemPrompt` zostaje
  bajt w bajt dzisiejszy (twarda asercja w testach; prompty strojone pod Bielika — zbędne zasady
  o nieistniejącej sekcji to ryzyko regresji, patrz 5e). Z dokumentem: `SystemPrompt` + doklejony
  blok `DocumentRules`:
  1. fakty stanu faktycznego czerp z sekcji DOKUMENT i cytuj [Dk];
  2. DOKUMENT NIE jest źródłem prawa — podstawę prawną cytuj wyłącznie z [n]; gdy treść dokumentu
     jest sprzeczna z przepisem, wskaż rozbieżność wprost;
  3. dostajesz FRAGMENTY dokumentu (nie całość) — jeśli pytanie dotyczy treści nieobecnej we
     fragmentach, napisz to wprost („dołączone fragmenty nie zawierają…”), nie zgaduj zawartości
     reszty pliku;
  4. fraza odmowy („Nie mam wystarczających źródeł…”) bez zmian — dotyczy braku PRAWA w źródłach,
     nie braków dokumentu.
- Testy: numeracja obu przestrzeni; sekcja i DocumentRules obecne TYLKO z załącznikiem; bez
  załącznika system prompt identyczny z dzisiejszym (Assert.Equal na stałej).

### DOC-3: `CitationValidator` — walidacja przestrzeni [Dk]
- Markery `[D\d+]` walidowane przeciw liczbie/treści fragmentów dokumentu (out-of-range jak dla [n]);
  istniejąca walidacja [n] bez zmian.
- Testy: [D2] w zakresie czysty, [D9] poza zakresem wykryty, mieszane [1]+[D1] czyste.

### DOC-4: `ChatService.AskAsync(..., DocumentContext? doc)` + zdarzenia
- Doc-retrieval przed retrievalem korpusu; `DocSourcesEvent(fragments)` obok `SourcesEvent`;
  walidacja obu przestrzeni w DoneEvent.
- Testy (fakes, wzorzec ChatServiceFollowUpTests): fragmenty [D] w prompcie, brak dokumentu ≡ dziś,
  abstynencja korpusu ma pierwszeństwo mimo obecności dokumentu.

### DOC-5: UI (Chat.razor)
- `InputFile` (accept=.pdf) przy polu pytania; chip załącznika (nazwa, strony, ✕ usuń);
  komunikat bramki skanów; sekcja „Twój dokument” w panelu źródeł (karty [D1..], styl source-card);
  klikalne `[Dk]` (rozszerzenie MarkdownRenderer o markery D — ta sama mechanika co [n]);
  informacja „załącznik żyje do końca sesji — nie zapisujemy treści”.
- `/o-systemie`: akapit o analizie dokumentów (co robi, czego nie: OCR, wiele plików).

### DOC-6: weryfikacja E2E (M4, checklist do dokumentu sesji)
1. Realna umowa (born-digital) + pytanie o konkretny paragraf → odpowiedź cytuje [D] (fakt) i [n] (prawo),
   oba klikalne; `PRAWORAG_DUMP_PROMPT=1` pokazuje sekcję DOKUMENT.
2. Skan → czytelna odmowa bez wywołania LLM.
3. Pytanie z dokumentem o prawo spoza korpusu → abstynencja mimo załącznika (decyzja #5).
4. Follow-up w tej samej rozmowie → doc-retrieval działa bez ponownego uploadu; po F5 dokument
   wymaga ponownego dołączenia (komunikat, nie błąd).
5. Golden-set `--chat` bez załącznika → brak regresji (overload z pustym dokumentem).

## Poza zakresem v1 (jawnie)
- OCR skanów (osobna decyzja — koszt/model; na razie uczciwa odmowa).
- Anonimizacja/pseudonimizacja przed LLM (kierunek #2 planu KAZ — osobny klocek).
- Wiele załączników naraz; formaty DOCX/RTF (technicznie łatwe, dokładać po feedbacku).
- Persystencja dokumentu między sesjami / re-upload po rekonekcie.
- PDF → tryb kazusu (v2: `DocumentContext` jako wejście KAZ — fundament projektowany pod to).

## Kolejność
DOC-0 → DOC-1 → DOC-2 → DOC-3 (fundament + prompt/walidacja, wszystko czysto testowalne) →
DOC-4 → DOC-5 (konsument) → DOC-6 na M4. Commit per task.

## Status (2026-07-18)
DOC-0…5 **zaimplementowane** (commity `955636c`, `afab2ad`, `19d30b0`, `973df86`); wszystkie czyste
testy zielone (236/236 na maszynie bez PG; T-DOC-0…4 + renderer [Dk]). Odstępstwo od planu, świadome:
chunker załączników to własny, znakowy `DocChunker` zamiast reużycia `TokenAwareChunker` — Ingestion
to projekt exe (konektory SAOS/ELI), a TEI ma `--auto-truncate`, więc konserwatywny limit znaków
degraduje bezpiecznie bez zależności Api→Ingestion. **Pozostało: DOC-6 (weryfikacja E2E na M4,
checklist wyżej) — dopiero po niej feature jest „wdrożony".**
