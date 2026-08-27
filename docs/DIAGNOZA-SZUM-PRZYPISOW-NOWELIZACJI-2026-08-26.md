# Diagnoza: trzy niezależne źródła szumu w treści chunków aktów (przypisy, glify, uszkodzone kodowanie)

Data: 2026-08-26. Dokument do przekazania agentowi/wykonawcy, który ma zaprojektować i wdrożyć
naprawę — poniżej wyłącznie ZMIERZONE fakty i zweryfikowany w kodzie stan obecny, żadnych zmian
nie wprowadzono (backfill i poprawki normalizatora są celowo NIE zrobione — patrz sekcja „Otwarte").
Trzy problemy, prawdopodobnie NIEZALEŻNE od siebie (różne mechanizmy, różne pliki źródłowe) —
opisane w jednym dokumencie, bo wyszły z tego samego systematycznego sprawdzenia jakości danych.

## Kontekst, z którego to wynika

Podczas diagnozy osobnego incydentu retrievalowego (pytanie o limit wpłat na Osobiste Konto
Inwestycyjne — ustawa poprawnie trafiona w top-1, ale właściwy artykuł z limitem przegrał z
sąsiednim, niewłaściwym artykułem tego samego aktu różnicą cosine rzędu 0,05) padło pytanie: ile
jeszcze tego typu problemów jakości danych może siedzieć w korpusie, nieznalezionych przez
przypadek. Poniższe trzy problemy wyszły z systematycznego, a nie przypadkowego, sprawdzenia.

Korpus: Postgres + pgvector, tabele `chunks`/`documents`, ~7,4 mln wierszy `chunks` łącznie
(akty ELI + orzeczenia SAOS/NSA). Embedding: `sdadas/mmlw-retrieval-roberta-large-v2` (okno 512
tok.). Chunking: `TokenAwareChunker` (`src/PrawoRAG.Ingestion/Chunking/TokenAwareChunker.cs`),
`TargetTokens=450`, `MaxTokens=512` (`ChunkerOptions.cs`).

## Problem 1 [ZMIERZONE — skala]: przypisy historii nowelizacji dominują treść chunka

### Objaw

Polska konwencja legislacyjna: przy pierwszym przywołaniu aktu bazowego w innym akcie (typowo w
ustawie nowelizującej) doklejany jest przypis w formie „(Dz.U. Nr 43, poz. 296, z późn. zm.)
zmiany wymienionej ustawy zostały ogłoszone w Dz. U. z 1965 r. Nr 15, poz. 113, z 1974 r. Nr 27,
poz. 157 i Nr 39, poz. 231, (...)" — czysto bibliograficzna lista numerów Dziennika Ustaw, zero
treści normatywnej. Ten przypis trafia do `chunks.Text` bez żadnego przetworzenia i bywa
DŁUŻSZY niż właściwa treść merytoryczna, z którą dzieli chunk.

### Przykład (zmierzony na żywej bazie, `documents`/`chunks`)

Chunk `TokenCount=467` — z 467 tokenów zdecydowana większość to lista „Nr X, poz. Y", a właściwa
treść merytoryczna (wyrok TK) to jedno zdanie na końcu:

```
1. Art. 1 ustawy z dnia 17 listopada 1964 r. – Kodeks postępowania cywilnego (Dz.U. Nr 43,
poz. 296; zm.: z 1965 r. Nr 15, poz. 113; z 1974 r. Nr 27, poz. 157, Nr 39, poz. 231; [...ok. 60
dalszych pozycji „Nr X, poz. Y" ciągnących się do 2000 r....]), rozumiany w ten sposób, iż w
zakresie pojęcia "sprawy cywilnej" nie mogą się mieścić roszczenia dotyczące zobowiązań
pieniężnych, których źródło stanowi decyzja administracyjna, jest niezgodny z art. 45 ust. 1 w
związku z art. 31 ust. 3 Konstytucji Rzeczypospolitej Polskiej,
```

Embedding tego chunka reprezentuje statystycznie głównie „ciąg liczb i słowa Nr/poz.", nie
„wykładnia pojęcia sprawy cywilnej w k.p.c." — to obniża szansę, że chunk zostanie trafiony przez
pytanie o treść merytoryczną, którą faktycznie niesie.

### Skala (zmierzone zapytaniem SQL na całym korpusie aktów, `DocType='act'`)

| Miara | Wartość |
|---|---|
| Chunki aktów ogółem | 546 307 |
| Chunki z ≥5 wystąpieniami wzorca `poz\.\s*\d+` | 14 724 (2,7%) |
| Chunki z ≥10 wystąpieniami | 6 959 (1,27%) |
| Średnia liczba wystąpień w grupie ≥5 | 13 |

Metoda wyszukiwania DUPLIKATÓW tekstu między aktami (`GROUP BY` znormalizowany tekst,
`HAVING count(DISTINCT DocumentId) > próg`) **niedoszacowuje** to zjawisko — każdy wariant
przypisu ma inny punkt odcięcia historii nowelizacji (różne akty nowelizujące cytowały ten sam
akt bazowy w różnych momentach), więc rzadko są to dokładne duplikaty. Miarodajna jest gęstość
wzorca `poz\. N` per chunk, nie duplikacja tekstu.

### Zapytania SQL użyte do pomiaru

```sql
-- Gęstość wzorca "poz. N" w chunkach aktów (miara skali problemu)
SELECT
  count(*) FILTER (WHERE cnt >= 10) AS chunki_10plus_pozycji,
  count(*) FILTER (WHERE cnt >= 5)  AS chunki_5plus_pozycji,
  count(*)                          AS wszystkie_chunki_aktow,
  round(avg(cnt) FILTER (WHERE cnt >= 5)) AS srednia_pozycji_w_podejrzanych
FROM (
  SELECT c."Id", c."TokenCount",
         (SELECT count(*) FROM regexp_matches(c."Text", 'poz\.\s*\d+', 'g')) AS cnt
  FROM chunks c JOIN documents d ON d."Id" = c."DocumentId"
  WHERE d."DocType" = 'act'
) t;
-- Wynik: 6959 | 14724 | 546307 | 13

-- Wyszukiwanie duplikatów/near-duplikatów międzyaktowych (metoda pomocnicza, patrz zastrzeżenie wyżej)
SELECT lower(regexp_replace(c."Text", '\s+', ' ', 'g')) AS znormalizowany,
       count(DISTINCT c."DocumentId") AS liczba_aktow,
       min(left(c."Text", 100)) AS podglad
FROM chunks c JOIN documents d ON d."Id" = c."DocumentId"
WHERE d."DocType" = 'act'
GROUP BY znormalizowany
HAVING count(DISTINCT c."DocumentId") > 20
ORDER BY liczba_aktow DESC LIMIT 50;
```

### Mechanizm w kodzie (potwierdzone — brak jakiejkolwiek obróbki tego wzorca)

Sprawdzone oba normalizatory aktów ELI:

- `src/PrawoRAG.Ingestion/Eli/ActNormalizer.cs` (ścieżka HTML — większość korpusu, w tym stare
  teksty jednolite z lat 1990–2020, gdzie ten wzorzec występuje najczęściej) — brak jakiejkolwiek
  logiki dot. przypisów/footnote/odnośników (`grep` po `sup|footnote|przypis|odnośnik` — zero
  trafień poza nazwami regexów niezwiązanych z tematem).
- `src/PrawoRAG.Ingestion/Eli/ActTextParser.cs` (ścieżka PDF, teksty jednolite ELI od 2025) —
  ma `SkipPreamble` (usuwa treść PRZED pierwszym `Art.`/`§`, więc chroni przed preambułą
  obwieszczenia) i `UchylonyOnly` (pomija jednostki będące wyłącznie „(uchylony)"), ale ŻADNEGO
  mechanizmu na przypis wewnątrz treści artykułu/paragrafu, gdy przypis jest częścią BODY (jak w
  przykładzie wyżej — to fragment art. 1 ustawy nowelizującej, więc `SkipPreamble` go nie łapie).

Wniosek: przypis wchodzi do `NormalizedDocument.Segments` jako zwykły tekst, `TokenAwareChunker`
tnie go bez rozróżnienia od treści normatywnej. Nie ma dziś ŻADNEGO punktu w pipeline, który by
to filtrował.

### Sąsiednia, ale niewystarczająca infrastruktura

- `ChunkerOptions.MinSubstantiveWords` (`TokenAwareChunker.cs`) odrzuca całe chunki poniżej progu
  „sensownych słów" — ale przypis MA dużo słów (same liczby + „Nr"/„poz." to nie są odrzucane jako
  nie-słowa), więc przechodzi filtr bez przeszkód. To narzędzie na inny problem (zdegenerowane
  krótkie chunki), nie na ten.
- `ChunkClassifier.cs` (`PrawoRAG.Eval`, instrument CIT-2) klasyfikuje `RepealedOrOmitted`,
  `AmendmentVariant`, `AmendmentAct`, `ThinEnumeration` — żadna kategoria nie obejmuje „chunk
  zdominowany przez przypis bibliograficzny". Naturalne miejsce, żeby dodać piątą kategorię, ale
  dziś jej brak.
- `QualityReportRunner.cs` (`PrawoRAG.Ingestion`) to gotowy hak „raport PRZED masowym
  embeddingiem, bez bazy" — naturalne miejsce, żeby wpiąć tu stały check tego wzorca na przyszłość,
  zamiast polegać na przypadkowym odkryciu (tak jak dziś).

## Problem 2 [ZMIERZONE tylko jakościowo, skala NIEZMIERZONA]: pojedyncze niepożądane glify (np. „⚫")

### Objaw

Znak „⚫" (i prawdopodobnie pokrewne — checkboxy/bullet z ekstrakcji HTML/PDF formularzy) trafia
do `chunks.Text`. Już OPISANY w kodzie jako znany problem — komentarz w `ChunkerOptions.cs` przy
`MinSubstantiveWords` wprost: fragmenty z takimi glifami „mają anomalnie wysokie cosine do
KAŻDEGO zapytania i wypychają realne przepisy z top-K". Filtr `MinSubstantiveWords` odrzuca
jedynie CAŁE chunki zdegenerowane do samych takich znaków — nie usuwa glifu, gdy występuje
WEWNĄTRZ chunka, który poza tym ma sensowną treść (taki chunk przechodzi filtr, ale jego
embedding wciąż jest zanieczyszczony).

### Status pomiaru: ZMIERZONE (dwie niezależne próbki)

Próbka 1 (20 000 wierszy, `TABLESAMPLE SYSTEM(5) LIMIT 20000`, pełny sweep znaków — metoda opisana
niżej): 217 wystąpień / 49 chunków. Próbka 2, dokładniejsza i większa (200 000 wierszy, tylko
policzenie `LIKE '%⚫%'`, bez pełnego sweepu): **445 / 200 000 = 0,2225%**. Obie próbki zgadzają
się co do rzędu wielkości. Ekstrapolacja na cały korpus (~7,4 mln wierszy `chunks`):
**rząd wielkości 16 000–18 000 chunków**. To szacunek z próby, nie dokładna liczba — dokładną dałoby
dopiero pełne `COUNT(*) WHERE "Text" LIKE '%⚫%'` bez samplingu (nieodpalone — ryzyko długiego czasu
wykonania na 7,4 mln wierszy, patrz zastrzeżenia o `statement_timeout` w sekcji Narzędzie).

```sql
-- Sweep wszystkich nietypowych znaków (użyty do znalezienia "⚫" ORAZ Problemu 3 niżej) —
-- musi być ograniczony (TABLESAMPLE + LIMIT), inaczej pełny regexp_split_to_table na 7,4 mln
-- wierszy nie kończy się w rozsądnym czasie (zmierzone: timeout przy naiwnej wersji bez limitu)
SET statement_timeout = '120s';
SELECT ch, count(*) AS wystapien, count(DISTINCT chunk_id) AS chunkow
FROM (
  SELECT chunk_id, regexp_split_to_table(residual, '') AS ch
  FROM (
    SELECT id AS chunk_id,
           regexp_replace(txt,
             '[A-Za-zĄąĆćĘęŁłŃńÓóŚśŹźŻż0-9\s.,;:()§%/–—„”?*+=<>@#&_-]', '', 'g') AS residual
    FROM (SELECT c."Id" AS id, c."Text" AS txt FROM chunks c TABLESAMPLE SYSTEM (5) LIMIT 20000) base
  ) r
  WHERE residual <> ''
) t
GROUP BY ch ORDER BY wystapien DESC LIMIT 60;

-- Dokładniejsza próbka tylko dla "⚫" (tańsze niż pełny sweep, bo bez regexp_split_to_table)
SELECT count(*) FILTER (WHERE txt LIKE '%⚫%') AS chunki_z_kulka, count(*) AS probka
FROM (SELECT c."Text" AS txt FROM chunks c TABLESAMPLE SYSTEM (20) LIMIT 200000) s;
```

Uwaga: górna część wyniku sweepu to zwykła, oczekiwana interpunkcja (przecinki, nawiasy, §, %,
myślniki) — reszta listy (poniżej pozycji ~10) to w większości albo „⚫", albo znaki opisane w
Problemie 3 niżej.

## Problem 3 [ZMIERZONE — nowe, prawdopodobnie WIĘKSZE niż Problem 2]: uszkodzenie kodowania znaków (mojibake), skoncentrowane w torze ELI

### Objaw

Ten sam sweep znaków (Problem 2) ujawnił rodzinę znaków, których nie ma w naturalnym polskim
tekście prawnym: `∏ à ´ Ê ˝ ƒ ç Â Ñ é` oraz — osobno sprawdzone, patrz niżej — zakres cyrylicy.
Współwystępowanie akurat TEJ rodziny (zwłaszcza `Â` poprzedzające inny znak) to klasyczny objaw
podwójnego/błędnego dekodowania UTF-8 (bajty UTF-8 zinterpretowane w złym kodowaniu jednobajtowym,
np. Windows-1250/ISO-8859-2, i odwrotnie).

### [HIPOTEZA OBALONA] „To obce (rosyjskojęzyczne) dokumenty w korpusie"

Pierwotna interpretacja znaków z zakresu cyrylicy (`о е а и н т с л р в д п я м к у г з`, każdy w
DOKŁADNIE 6 chunkach w próbce) — że to garstka błędnie zakwalifikowanych rosyjskojęzycznych
dokumentów. **Sprawdzone bezpośrednio i obalone**: zapytanie o konkretne dokumenty pokazało wyłącznie
zwykłe polskie akty i orzeczenia (rozporządzenia MSWiA, ustawy, wyroki SO/SR — pełne polskie tytuły).
Cyrylica nie jest osobnym zjawiskiem — to WARIANT tego samego uszkodzenia kodowania z Problemu 3:
różne przesunięcia bajtowe przy błędnym dekodowaniu potrafią wylądować zarówno w zakresie
łacińskim-z-akcentem, jak i w zakresie cyrylicy, zależnie od konkretnych bajtów źródłowych.

### Skala i koncentracja źródłowa (zmierzone)

Próbka `TABLESAMPLE SYSTEM(5)` (bez `LIMIT` — sweep dopasowań regexowych, tańszy niż pełny split,
zmieścił się w czasie): **1234 chunki ELI/act, 65 SAOS/judgment, 2 NSA/judgment** (1301 razem).

Odnosząc do znanej liczby chunków aktów (546 307, Problem 1) i przyjmując, że 5% próbki objęło
proporcjonalną część aktów (~27 300 chunków aktów w próbce): **1234/27 300 ≈ 4,5% wszystkich
chunków aktów** ma ten wzorzec — ekstrapolacja rzędu **24 000–25 000 chunków**. To W PRZYBLIŻENIU
zgadza się z arytmetyką (1301 razem, 94,8% w ELI/act) — koncentracja w torze ELI jest wyraźna i
nieprzypadkowa: to NIE jest szum rozłożony równomiernie po całym korpusie, tylko problem
praktycznie ograniczony do ustaw/rozporządzeń.

Jeśli szacunek się potwierdzi, to WIĘKSZY problem niż „⚫" (Problem 2, ~17 tys.) i większy niż
przypisy nowelizacyjne (Problem 1, ~14,7 tys.) — i dotyczy WYŁĄCZNIE aktów, czyli dokładnie tego
typu dokumentów, na którym opiera się cała ścieżka wyszukiwania przepisów.

```sql
-- Koncentracja źródłowa mojibake
SET statement_timeout = '60s';
SELECT d."Source" AS zrodlo, d."DocType" AS typ, count(*) AS chunkow
FROM (
  SELECT c."DocumentId"
  FROM chunks c TABLESAMPLE SYSTEM (5)
  WHERE c."Text" ~ '[ÂÃàçêÑƒ´˝Ê∏]'
  LIMIT 5000
) hit
JOIN documents d ON d."Id" = hit."DocumentId"
GROUP BY d."Source", d."DocType"
ORDER BY chunkow DESC;
-- Wynik: ELI|act|1234   SAOS|judgment|65   NSA|judgment|2

-- Weryfikacja hipotezy "obce dokumenty" (obalona)
SELECT DISTINCT d."Id", d."Title", d."Source", d."DocType"
FROM chunks c TABLESAMPLE SYSTEM (10)
JOIN documents d ON d."Id" = c."DocumentId"
WHERE c."Text" ~ '[а-яА-Я]'
LIMIT 20;
-- Wynik: same polskie akty/orzeczenia (rozporządzenia MSWiA, ustawy, wyroki SO/SR)
```

### Mechanizm w kodzie: NIE ustalony precyzyjnie — dwóch kandydatów

Sprawdzone: `src/PrawoRAG.Ingestion/Eli/EliSejmConnector.cs` **nie ma żadnej jawnej obsługi
kodowania** (`grep` po `Encoding|charset|ISO-8859|Latin1|windows-125` — zero trafień poza
niezwiązanym `JsonElement.GetString()`). Odpowiedzi HTTP są czytane przez domyślne zachowanie
`HttpClient`, które zgaduje charset z nagłówka odpowiedzi (albo zakłada UTF-8 przy jego braku) —
to naturalny kandydat na źródło błędu przy złym/brakującym nagłówku po stronie API Sejmu.

DRUGI kandydat, NIEWYKLUCZONY: `Pdf.IPdfTextExtractor` — część aktów (teksty jednolite od 2025)
pochodzi z PDF, a błędy w mapowaniu CMap/font encoding przy ekstrakcji tekstu z PDF to osobny,
częsty mechanizm powstawania podobnych artefaktów, niezależny od HTTP.

**Nie rozstrzygnięto, który z dwóch to faktyczny winowajca** — wymaga sprawdzenia surowych bajtów
odpowiedzi HTTP dla jednego ze zidentyfikowanych dokumentów (np. `019f579c-00f5-72ff-9ed3-8ce46d23286d`,
rozporządzenie MSWiA) i porównania ze ścieżką ingestii (HTML czy PDF), zanim ktokolwiek zacznie
pisać poprawkę.

### WAŻNE: to NIE jest problem, który da się naprawić przez usunięcie znaków

W przeciwieństwie do Problemu 1 (bibliografia — bezpiecznie wyciąć) i Problemu 2 (glify formularzy —
bezpiecznie wyciąć), uszkodzony znak w mojibake zazwyczaj REPREZENTUJE prawdziwą literę (np. „ą")
źle zdekodowaną. Zwykłe usunięcie znaku zostawi słowo OKALECZONE („pastwo" zamiast „państwo"),
psując i BM25, i embedding jeszcze bardziej niż samo uszkodzenie. Właściwa naprawa to albo:
(a) ponowna ingestia z poprawnym kodowaniem, jeśli surowe źródło wciąż dostępne (najlepsze, ale
wymaga ponownego pobrania/ekstrakcji dotkniętych dokumentów), albo (b) odwrotna transkodyzacja
mojibake (znane, ale niepewne technicznie przy niejednoznacznych przypadkach) — NIE prosty
regex-strip jak w Problemach 1–2.

## Dlaczego to może mieć znaczenie dla jakości odpowiedzi (hipoteza, NIE dowód)

Wszystkie trzy problemy obniżają stosunek sygnału do szumu w embeddingu chunka, który POZA tym
niesie prawdziwą treść normatywną (Problem 3 — mojibake w akcie — robi to najbardziej dotkliwie,
bo psuje same słowa, nie tylko dokłada balast obok nich). To spójne z niezależnie zaobserwowanym
w tej samej sesji diagnostycznej przypadkiem: artykuł z limitem wpłat na OKI przegrał rankingiem
z sąsiednim artykułem tego samego aktu różnicą cosine ~0,05 — mechanizm, w którym embedding nie
odróżnia "pewnie trafne" od "faktycznie poprawne", byłby WZMOCNIONY przez każdy z tych trzech
rodzajów szumu. **To nie jest udowodniony wspólny mechanizm przyczynowy** — nie sprawdzono
bezpośrednio, czy sporny artykuł OKI miał przypis, glif czy uszkodzone kodowanie — to wyłącznie
uzasadnienie, dlaczego warto to naprawić, nie potwierdzona przyczyna konkretnego incydentu.

## Otwarte — kierunek naprawy nierozstrzygnięty

Nie oceniam, która opcja słuszna, bez dodatkowych danych/decyzji projektowej:

1. **Gdzie usuwać przypis**: (a) całkowicie wyciąć z `Text` przed embeddingiem — traci się
   możliwość pokazania prawnikowi pełnej historii zmian, gdyby kiedyś była potrzebna; (b)
   przenieść do osobnego pola metadanych (nowa kolumna albo `TypedMetadata`) — zachowuje
   dostępność, wymaga zmiany schematu i UI źródeł.
2. **Sygnatura do wykrywania**: gęstość wzorca `Nr?\s*\d+,?\s*poz\.\s*\d+` powyżej progu (dane z
   tej diagnozy sugerują próg rzędu 5 jako rozsądny punkt startowy — 2,7% chunków aktów) ALBO
   fraza-wyzwalacz „zmian(y|a) (wymienionej|niniejszej) ustawy (zostały|została) ogłoszon(e|a) w
   Dz\.\s*U\." jako kotwica początku bloku do wycięcia. Do zweryfikowania na próbce.
3. **Zakres naprawy**: to NIE wymaga pełnego reindeksu korpusu (7,4 mln wierszy) — ~14,7 tys.
   chunków (2,7% aktów, 0,2% całości) to podzbiór do:
   - poprawki w `ActNormalizer.cs` (ścieżka HTML) i `ActTextParser.cs` (ścieżka PDF) — żeby
     przyszłe ingesty (kolejne nowelizacje k.p.c., k.c., k.k. itd.) nie odtwarzały wzorca,
   - backfillu istniejących chunków: oczyszczenie `Text`, przeliczenie `TokenCount`
     (`IEmbeddingProvider.CountTokensAsync`), ponowny embedding wyłącznie tego podzbioru
     (`IEmbeddingProvider` — patrz `TeiEmbeddingProvider.cs`), `UPDATE` w `chunks`.
4. **Kolejność bezpieczeństwa**: przed dotknięciem `ActNormalizer.cs`/`ActTextParser.cs` i przed
   backfillem sprawdzić, czy proces ingestii nie działa w tle (`ps aux | grep PrawoRAG.Ingestion`,
   świeżość plików w `logs/`/`src/PrawoRAG.Ingestion/logs/`) — to kod na ścieżce ingestii, projekt
   ma zasadę nieingerowania w logikę fetch/resume w trakcie jej działania (ryzyko utraty danych).
5. **Problem 2 („⚫")**: skala zmierzona (~16–18 tys. chunków, patrz wyżej). Prawdopodobnie ten sam
   wzorzec naprawy (backfill podzbioru + poprawka normalizatora), ale inne źródło (ekstrakcja
   HTML/PDF formularzy, nie konwencja legislacyjna) — inny normalizator może być winowajcą niż
   w Problemie 1. Dokładna lokalizacja źródła NIE ustalona (w przeciwieństwie do Problemu 1, gdzie
   sprawdzono `ActNormalizer.cs`/`ActTextParser.cs` wprost) — do zrobienia analogicznie.
6. **Problem 3 (mojibake) — priorytet, bo prawdopodobnie największy i dotyka WYŁĄCZNIE aktów**:
   - Najpierw rozstrzygnąć root cause: sprawdzić surowe bajty odpowiedzi HTTP dla
     `019f579c-00f5-72ff-9ed3-8ce46d23286d` (rozporządzenie MSWiA, potwierdzony nośnik problemu) —
     ustalić, czy to ścieżka HTML (`EliSejmConnector.cs` + `ActNormalizer.cs`, brak jawnego
     `Encoding`) czy PDF (`IPdfTextExtractor`). To DWIE różne naprawy w DWÓCH różnych miejscach.
   - Zdecydować strategię naprawy PRZED pisaniem kodu: prosty regex-strip (jak Problem 1/2) tu
     NIE działa (patrz „WAŻNE" wyżej) — potrzebna albo re-ingestia dotkniętych dokumentów z
     poprawnym kodowaniem, albo odwrotna transkodyzacja (ryzykowna przy niejednoznacznych
     przypadkach — wymaga walidacji na próbce ZNANYCH dobrych/złych par przed zastosowaniem
     masowo).
   - Uzyskać dokładną liczbę (nie tylko ekstrapolację z 5% próbki) przed decyzją o skali backfillu —
     `SELECT count(*) FROM chunks c JOIN documents d ON d."Id"=c."DocumentId" WHERE d."DocType"='act' AND c."Text" ~ '[ÂÃàçêÑƒ´˝Ê∏]'`
     z odpowiednim `TABLESAMPLE`/`LIMIT`, bo pełny skan może się nie zmieścić w rozsądnym czasie
     (patrz doświadczenie z tego dokumentu — najpierw ograniczaj, potem zwiększaj).

## Narzędzie

Wszystkie zapytania w tym dokumencie to zwykłe `psql` na produkcyjnej bazie tylko do odczytu
(`192.168.100.11`), bez zmian w kodzie czy danych. `ChunkClassifier.cs` (`PrawoRAG.Eval`) i
`QualityReportRunner.cs` (`PrawoRAG.Ingestion`) to istniejąca infrastruktura, którą warto
rozszerzyć zamiast budować równoległy mechanizm.
