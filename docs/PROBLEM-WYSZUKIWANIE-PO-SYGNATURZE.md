# Problem: orzeczenia nie są znajdowalne po WŁASNEJ sygnaturze

Data: 2026-07-23. Znalezione przy weryfikacji jakości ingestii NSA, PRZED pełnym runem (~650 tys.
wyroków). Dotyczy **wszystkich źródeł orzeczniczych** (SAOS i NSA), nie tylko nowej ingestii.
Bez zmian w kodzie — dokument opisuje ustalony stan faktyczny i warianty do decyzji.

## Objaw

Zapytanie o konkretną sygnaturę nie zwraca tego orzeczenia. Prawnik podający „III SA/Po 154/26"
dostaje albo nic, albo cudze wyroki, które tę sygnaturę cytują.

## Dowody (zmierzone na żywej bazie, 2026-07-23)

**NSA** — orzeczenie `III SA/Po 154/26` jest w korpusie (15 chunków, status Indexed), a mimo to:

```sql
SELECT count(*) FROM chunks c
WHERE c."SearchVector" @@ websearch_to_tsquery('simple','III SA/Po 154/26');
-- 0 trafień
```

**SAOS** — to samo, tylko mylące: zapytanie o `II AKa 137/16` zwraca 6 chunków, ale
`position('II AKa 137/16' in c."Text")` pokazuje, że:
- 4 z nich w ogóle nie zawierają tego ciągu (zapytanie to koniunkcja `'ii' & 'aka' & '137/16'`,
  a te tokeny trafiają się rozproszone w niepowiązanym tekście);
- 2 zawierają go naprawdę — ale to INNE wyroki (`VI Ka 1366/19`, `XVII Ka 1194/16`), które
  orzeczenie `II AKa 137/16` **cytują**;
- **żadne z 6 nie jest samym orzeczeniem `II AKa 137/16`.**

Wniosek: znajdujemy wyłącznie CUDZE odwołania do sygnatury, nigdy orzeczenia po jego własnej.

## Przyczyna źródłowa

`DocumentSegment.ContextHeader` (`src/PrawoRAG.Domain/Documents/DocumentSegment.cs:24`) niesie
„sąd — sygnatura" i jest USTAWIANY przez wszystkie normalizery:

- `Nsa/NsaNormalizer.cs:98` — „Wojewódzki Sąd Administracyjny w Poznaniu — III SA/Po 154/26"
- `Saos/JudgmentNormalizer.cs:118`
- `Eli/ActNormalizer.cs:190`, `Eli/ActTextParser.cs:107`

…ale **nie jest czytany przez nikogo**. `TokenAwareChunker` buduje `Text = packed.Text` wyłącznie
z `segment.Text` — nagłówek nigdy nie trafia ani do treści chunka, ani do `SearchVector`, ani do
embeddingu. Komentarz w `NsaNormalizer.cs:83` mówi wprost „ContextHeader doklejany do każdego
chunka (samowystarczalność dla retrievalu i cytatu)" — i to założenie jest NIESPEŁNIONE.

Sygnatura trafia więc do tekstu tylko przypadkiem, gdy sama treść orzeczenia ją wymienia. Pomiar
na SAOS pokazuje, jak bardzo to loteria (chunki zawierające własną sygnaturę / wszystkie chunki
dokumentu): 2/25, 13/18, 0/2, 0/29, 0/2.

Dodatkowo **retrieval nie ma dedykowanej ścieżki po sygnaturze** — `CaseNumber`/`caseNumber` nie
występuje w `PrawoRAG.Storage/Retrieval/` ani `PrawoRAG.Domain/Retrieval/`. Sygnatura żyje w
`documents."TypedMetadata"` i w `CitationLocator`, ale nic jej nie przeszukuje.

Co NIE jest przyczyną: tokenizacja. Konfiguracja `simple` radzi sobie z sygnaturami poprawnie —
`to_tsvector('simple','… III SA/Po 154/26 …')` daje `'iii' 'sa/po' '154/26'`, a
`websearch_to_tsquery` tworzy dopasowującą się koniunkcję. Gdyby sygnatura była w tekście,
znalazłaby się.

## Dlaczego to blokuje

1. **To podstawowy sposób wyszukiwania w praktyce prawniczej.** Sygnatura jest identyfikatorem
   orzeczenia — pismo procesowe drugiej strony powołuje sygnatury, nie streszczenia.
2. **Skaluje się źle.** Przy 303 dokumentach NSA problem jest niewidoczny (i tak nie przebijają
   się przez 7,4 mln chunków). Przy pełnych ~650 tys. wyroków użytkownik BĘDZIE oczekiwał, że
   poda sygnaturę i dostanie wyrok.
3. **Naprawa po fakcie = powtórka ingestii.** Doklejenie nagłówka do treści chunka zmienia
   `ContentHash`… nie, zmienia sam tekst chunka → wymaga PONOWNEGO CHUNKOWANIA I EMBEDOWANIA
   całego korpusu (7,4 mln + 7,6 mln nowych chunków). Zrobienie tego PRZED pełnym runem NSA
   kosztuje re-embedding istniejącego korpusu; zrobienie PO — re-embedding dwa razy większego.
   To ten sam argument, który zadecydował o naprawie `extracted_legal_bases` przed runem
   (commit `65e4757`).

## Warianty rozwiązania (do decyzji, z kosztami)

| wariant | na czym polega | koszt | ryzyko |
|---|---|---|---|
| A. Nagłówek do treści chunka | `TokenAwareChunker` skleja `ContextHeader` + tekst (zgodnie z pierwotną intencją komentarzy w normalizerach) | pełny re-chunking + re-embedding korpusu | nagłówek w KAŻDYM chunku zmienia embedding — trzeba zmierzyć, czy nie psuje trafności semantycznej (ryzyko: powtarzalny prefiks zbliża do siebie wszystkie chunki dokumentu) |
| B. Osobna ścieżka po sygnaturze w retrieverze | lane szukający po `documents."TypedMetadata"->>'caseNumber'` (indeks + normalizacja zapisu), scalany do RRF jak lane akronimowy | bez re-embeddingu; praca w `HybridRetriever` + indeks | trzeba znormalizować warianty zapisu („III SA/Po 154/26" vs „III SA/Po 154/26") i wykryć sygnaturę w pytaniu |
| C. Nagłówek TYLKO do `SearchVector` | treść chunka bez zmian, ale wektor wyszukiwania budowany z nagłówek+tekst | bez re-embeddingu (embedding niezmieniony), re-generacja tsvector | wymaga rozdzielenia „tekst do embeddingu" od „tekst do BM25" — dziś to jedno pole |

Rekomendacja do rozważenia: **B** jako pierwsze — nie wymaga dotykania embeddingów, więc nie
blokuje pełnego runu NSA i jest odwracalne. A/C jako osobna decyzja, poparta pomiarem (wariant A
zmienia trafność semantyczną i bez ewaluacji to zgadywanie).

## Do rozstrzygnięcia przed pełnym runem NSA

- Czy ruszamy z pełną ingestią wiedząc, że wyszukiwanie po sygnaturze nie działa (i naprawiamy
  wariantem B, bez re-embeddingu)?
- Czy wariant A jest w ogóle akceptowalny bez ewaluacji wpływu na trafność (`PrawoRAG.Eval`,
  zbiór egzaminacyjny + `refusal-set.json`)?

## Powiązane

- [ANALIZA-INGESTIA-CBOSA.md](ANALIZA-INGESTIA-CBOSA.md) — strategia ingestii NSA/WSA
- [RUNBOOK-INGESTIA-NSA.md](RUNBOOK-INGESTIA-NSA.md) — procedura, w której ten problem wyszedł
- [PLAN-JAKOSC-RETRIEVALU.md](PLAN-JAKOSC-RETRIEVALU.md) — poprzednie prace nad jakością retrievalu
