# Plan: Query Understanding + retrieval strukturalny — zadania

Metodyczne rozwiązanie problemu: gdy użytkownik pyta o **konkretny artykuł ustawy** („co mówi art. 94 § 2
KW"), retrieval semantyczny pudłuje — dopasowuje kształt cytatu w losowych aktach. Do przeglądu przed
implementacją.

## Diagnoza awarii (potwierdzona z kodu + danych z M4)

Zmierzone: pytanie „a co z Art. 94 § 2 w zw. z § 1 Kodeksu wykroczeń" → maxSim **0,913** (wyżej niż dobra
odpowiedź 0,789!), ale 1/12 z KW i to zły przepis; reszta losowa (KPC, KK).

Przyczyna (z `HybridRetriever.cs`):
- Tor rzadki (BM25) używa `WebSearchToTsQuery` na konfiguracji `simple` → tokeny łączone przez **AND** +
  **zero stemmingu**. Pytanie tokenizuje się na `a, co, z, art, 94, 2, w, zw, 1, kodeksu, wykroczeń`.
  Nagłówek chunka to „Kodeks wykroczeń, Art. 94 § 2" — jest „kodeks", nie ma „kodeksu" → AND pada → **tor
  rzadki nie zwrócił art. 94 w ogóle**.
- Cały wynik pochodził z toru gęstego, a ten dopasował *kształt* cytatu („art. N § M w zw. z…") — stąd
  wysoki, mylący sim na śmieciach.

Wniosek: to nie jest tylko „słaby polski BM25". Koniunkcję „artykuł=94 **I** akt=KW" gwarantuje jedynie
**deterministyczny filtr po metadanych**, które już mamy w lokalizatorach chunków. Semantyka i BM25 tego
nie wymuszą. Metadane są gotowe w magazynie — brakuje warstwy, która **z pytania** wyłuska lokalizator.

## Architektura: rozumienie zapytania → routing → fuzja

Przestajemy wpychać każde pytanie w jedną rurę semantyczną. Warstwa na wejściu rozkłada pytanie i kieruje
części do właściwych narzędzi:

1. **Analiza zapytania** → `{ cytaty[], filtry, tekst semantyczny }`. **Deterministycznie** (regex numer +
   aliasy/pg_trgm dla aktu); LLM tylko jako fallback, gdy deterministyka nic nie rozpozna.
2. **Tor strukturalny** — dla każdego cytatu: dokładny filtr po metadanych (Article + akt) → CAŁY artykuł.
3. **Tor semantyczny** — jak dziś (dense + BM25 + RRF) dla części pojęciowej.
4. **Fuzja addytywna** — trafienia strukturalne dostają zarezerwowane sloty na górze; semantyczne nigdy nie
   są usuwane. Brak rozpoznania → zachowanie identyczne jak dziś (zero regresji).

## Kto miał rację (dla protokołu)

- **Agent (BM25-PL):** trafny kierunek (tor rzadki okaleczony), ale sam słownik nie wymusi koniunkcji
  art.+akt i ma koszt infra (własny obraz Postgres). Wtórne, po pomiarze.
- **Użytkownik (metadane, metodycznie):** właściwy fundament — koniunkcję daje tylko filtr po metadanych.
- **LLM na gorącej ścieżce (wcześniejsza propozycja):** błąd akcentu — latencja Bielika, niedeterminizm,
  kapryśny structured output. LLM schodzi do roli fallbacku.

## Poprawki wcielone w plan (P1–P10)

- **P1** Ekstrakcja deterministyczna najpierw (regex numer + aliasy + pg_trgm do tytułów aktów); LLM = ogon.
- **P2** Ekstraktor zwraca **listę** cytatów („§ 2 w zw. z § 1", „art. 94 i 95").
- **P3** Pobieramy **cały artykuł**, nie tylko cytowany § (przepisy odsyłają do sąsiadów).
- **P4** Numer artykułu jako **tekst** (case-insensitive) — bywa „43bb", „175da".
- **P5** Tor strukturalny **omija** `MinChunkTokens` — krótki § nie może wypaść.
- **P6** Fuzja addytywna z gwarantowanym miejscem; semantyka nietknięta; brak rozpoznania = jak dziś.
- **P7** Multi-turn minimalnie: „a co z art. 94?" bez aktu → akt z poprzedniej tury (okno kontekstu).
- **P8** Szybka łatka toru rzadkiego: ts_query z samych tokenów znaczących / OR (zanim polski słownik).
- **P9** Pomiar w E5: kategoria **CitationQuery** + wariant dopytania (dowód naprawy klasy, nie przykładu).
- **P10** Storage: zdenormalizować `Article`/akt do indeksowanych kolumn chunka (tani dokładny filtr).

## Zadania per epik

### QU-0 — Ekstraktor cytatów (czysty, testowalny; P1/P2/P4)
- [QU-0.1] `CitationRef { Article, Paragraph?, Point?, ActHint? }` + `CitationParser.Parse(text) → IReadOnlyList<CitationRef>` (Domain, bez zależności).
  Regex tolerancyjny: „art"/„art."/„artykuł" + numer (`\d+[a-z]*`), wielokrotny; § jako informacja. ActHint = fraza „kodeks…" lub token-skrót (KW, k.w., KK…).
- [QU-0.2] Testy: warianty zapisu (skróty, brak kropek, odmiana w ActHint, wiele artykułów, „w zw. z"). *Kryt.: łapie art. 94 + akt „wykroczeń" z realnego pytania z M4.*

### QU-1 — Storage: metadane jako pierwszorzędny wymiar (P10)
- [QU-1.1] Denormalizacja `ArticleNo` (string) + `ActKey` (eli_id / skrót) na `ChunkEntity` z lokalizatora; indeks. Migracja + wypełnienie z istniejących (backfill w `process`/jednorazowo).
- [QU-1.2] `pg_trgm` (rozszerzenie) do rozmytego dopasowania ActHint → tytuł aktu. *Kryt.: „kodeksu wykroczeń" ~ „Kodeks wykroczeń" rozpoznane.*

### QU-2 — Rozpoznanie aktu (P1)
- [QU-2.1] Mapa aliasów (KW/KK/KPK/KPC/KC/KP/KSH/KKW/KRO/Ordynacja…) → token tytułu. Szybka ścieżka.
- [QU-2.2] Fallback pg_trgm: ActHint → najlepiej pasujący `Document` (docType=act) po tytule. Null gdy brak pewnego dopasowania (→ ścieżka strukturalna po prostu nie odpala).

### QU-3 — Tor strukturalny + fuzja (P3/P5/P6)
- [QU-3.1] `RetrievalQuery` += pola strukturalne (lista cytatów rozwiązanych na {ActKey?, Article}).
- [QU-3.2] `HybridRetriever`: dla każdego cytatu pobierz WSZYSTKIE chunki (Article==N [+ ActKey]), z pominięciem `MinChunkTokens`; wstaw na zarezerwowane sloty top-K; dołącz do semantycznych (dedup). *Kryt.: „art. 94 KW" → art. 94 KW na górze; regresja: pytania semantyczne bez zmian.*

### QU-4 — Pomiar (P9)
- [QU-4.1] Golden set: kategoria `CitationQuery` (co mówi art. X aktu Y) + wariant dopytania. Metryka: czy właściwy artykuł w top-K. *Kryt.: recall CitationQuery mierzony przed/po.*

### QU-5 — Multi-turn (P7)
- [QU-5.1] Analiza dostaje okno ostatnich wiadomości; cytat bez aktu dziedziczy akt z poprzedniej tury. *Kryt.: „a co z art. 94?" po pytaniu o KW trafia w KW.*

### QU-6 — Łatka toru rzadkiego (P8)
- [QU-6.1] Budowa ts_query z tokenów znaczących (odsiew stopwords/interpunkcji) lub OR. *Kryt.: jedna odmieniona forma nie zeruje AND.* Polski słownik full-text = osobna decyzja infra PO pomiarze.

### QU-7 — LLM fallback (tylko jeśli pomiar wymaga)
- [QU-7.1] Gdy deterministyczna ekstrakcja nic nie da, a pytanie wygląda na cytat → jedno wywołanie LLM (structured) → CitationRef. Bramkowane, nie na każdej ścieżce.

## Kolejność

**QU-0 (ekstraktor+testy) → QU-1 (storage) → QU-2 (akt) → QU-3 (tor+fuzja) → QU-4 (pomiar) → QU-5 (multi-turn) → QU-6 (BM25) → QU-7 (LLM tylko jeśli trzeba).**

## Zasada bezpieczeństwa

Tor strukturalny **tylko DOKŁADA** dokładne trafienia. Jeśli ekstrakcja/rozpoznanie zawiedzie albo aktu nie
ma w korpusie — retrieval zachowuje się dokładnie jak dziś. Zero regresji dla pytań semantycznych.
