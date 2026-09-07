# Rozszerzenie sąsiedztwa artykułów w aktach (SAS) — plan implementacji

**Data:** 2026-08-26. **Branch bazowy:** `feat/halfvec-retriever`.

## Status: ZAIMPLEMENTOWANE (Zadania 1–4), 693/693 testów zielone

Commity `b1da869`..`30866f3`. Flagi domyślne: `NeighbourhoodRadius = 2`,
`NeighbourhoodMinChunks = 3`, `NeighbourhoodTokenBudget = 20 000`. Mechanizm jest **włączony
domyślnie** — może tylko DODAĆ artykuły do kontekstu, bramki działają na końcu bez zmian, a warunek
koncentracji nie dotyka pytań z rozproszonymi źródłami.

**Do sprawdzenia przez Ciebie (jedno pytanie, bez evalu):** zapytać o limity wpłat na OKI. Albo
w źródłach jest przepis o progu, albo nie ma.

### Znaleziska z implementacji

1. **`RetrievedChunk` nie miał `ChunkIndex`.** Bez pozycji w dokumencie nie da się ani zaplanować
   sąsiedztwa, ani uporządkować aktu liniowo — pole doszło przez `ChunkRow` i oba mappery.
2. **`/api/search` używa tego samego `ToQuery` co czat**, więc sąsiedztwo trzeba było wyłączyć tam
   JAWNIE (`with { NeighbourhoodRadius = 0 }`). W Wyszukiwarce wynikiem jest lista trafień dla
   człowieka — dociąganie rozmyłoby ranking.
3. **Zadanie 4 okazało się szersze niż zgłoszenie.** Grupa `[2, 3, 4]` była niewidoczna nie tylko dla
   renderera (objaw: brak linku), ale i dla `CitationValidator` — czyli numer spoza zakresu ukryty
   w grupie (`[2, 99]`) nie trafiał do `OutOfRange` i `AnswerGate` przepuszczał odpowiedź cytującą
   nieistniejące źródło. Jest na to test regresji.
4. **Dwa błędy w moich własnych testach**, wykryte tylko przez pełny zestaw (w izolacji przechodziły):
   asercje na globalnej liczbie chunków są niestabilne, bo baza `LiveDb` jest współdzielona; oraz
   testowy akt musi być WIĘKSZY niż `TopK`, inaczej cały wchodzi do wyniku sam i sąsiedztwo nie ma
   czego dołożyć. Oba to przypomnienie, że test na wspólnej bazie musi mówić o SWOICH danych.

## Przypadek, który to wywołał

Pytanie o **limity wpłat na OKI** (osobiste konta inwestycyjne). Retrieval zadziałał: **8 z 8 źródeł
przyszło z właściwej ustawy**. Ale ani jedno nie zawierało limitu wpłat — bo w ustawie ten limit
nazywa się inaczej („próg zwolnienia z podatku"), więc semantycznie nie pasował do frazy „limit wpłat".

Dwie liczby, które rozstrzygają diagnozę:

- Chunk to `TargetTokens = 450`, a w polskim tekście prawnym słowo to ~2–2,5 tokena, więc **8 źródeł
  ≈ 1400–1800 słów**. Ustawa o OKI ma 18 stron ≈ 5,5–7 tys. słów. Model widział **jakąś piątą część
  ustawy** — nie „prawie całość", jak można by założyć.
- Pominięcie było **systematyczne, nie losowe**: kryterium wyboru (podobieństwo do sformułowania
  użytkownika) jest ślepe na synonim ustawowy. Dodanie kolejnych podobnych chunków tego nie naprawi.

## Dlaczego NIE zmiana modelu embeddingu / większe chunki

Rozważany był model z większym oknem, pozwalający na większe chunki. **Dla aktów prawnych to nic nie
zmieni** — i to jest fakt z kodu, nie opinia:

- `ActNormalizer` tworzy segmenty `Kind = "article"`, czyli **jeden artykuł = jeden segment**;
- `TokenAwareChunker` ma zasadę „chunki nie przekraczają granic segmentu".

Czyli chunki aktów są **już dziś wielkości artykułu**, ograniczone strukturą aktu, a nie limitem 512
tokenów modelu mmlw. Model z oknem 8k wyprodukowałby dla ustaw **identyczne chunki**. Przeembedowanie
7,5 mln chunków (dni GPU + 12,5 h przebudowy HNSW + rewizja golden setu) nie tknęłoby przypadku,
który je motywował.

Dla orzeczeń (segment = `section`, długie uzasadnienie pakowane do 450 tokenów) większe chunki miałyby
efekt — ale pogorszyłyby precyzję wyboru, która jest tam już dziś na granicy (art. 1a u.p.o.l.
wypadający o jedno miejsce, `ef_search` podniesiony 400 → 1000, most cytowań stworzony dlatego,
że przepis rządzący jest nieretrievalny dla pytań opisowych).

## Zasada: wyszukuj drobno, podawaj grubo

Dwa niezależne pokrętła, które łatwo pomylić:

| pokrętło | ograniczone przez | zmiana wymaga |
|---|---|---|
| **rozmiar chunka** — ile tekstu reprezentuje jeden wektor | okno embeddera (mmlw 512) **i struktura aktu** | re-embedding 7,5 mln chunków |
| **rozmiar kontekstu** — ile chunków wchodzi do promptu | okno modelu odpowiadającego | konfiguracja + jedna ścieżka kodu |

Bielik ograniczał **drugie**, nie pierwsze. Gemma 4 31B właśnie to odblokowała. Ten plan wykorzystuje
wyłącznie drugie pokrętło.

**Mechanizm:** w tekstach prawnych powiązane przepisy leżą **fizycznie obok siebie** — definicje,
wyjątki, progi i limity stoją przy przepisie, który modyfikują. Rozszerzenie sąsiedztwa omija problem
terminologii, bez wiedzy o terminologii.

## Architektura — jedna zmiana, bez nowych progów pewności

Po złożeniu finalnej listy `final` w `HybridRetriever`: dla **aktów, które dały ≥`MinChunksForExpansion`
źródeł**, dociągnij artykuły **sąsiadujące po `ChunkIndex`** (w obie strony), aż do budżetu tokenów.

Cztery świadome decyzje:

1. **Bez metryki „pewności identyfikacji aktu".** Nie identyfikujemy aktu — rozszerzamy to, co
   retrieval już wybrał. Koncentracja wyników (≥N z TopK z jednego dokumentu) JEST tym sygnałem
   i była obecna w przypadku OKI (8/8). Zero nowych progów do kalibracji od zera.
2. **Jeden mechanizm dla ustawy i kodeksu.** Dla 18-stronicowej ustawy trafienia są rozsiane po całym
   akcie, więc sąsiedztwo daje w praktyce cały akt. Dla kodeksu cywilnego ten sam kod dociągnie
   artykuły wokół trafień — i to jest zachowanie właściwe, nie degradacja. Nie ma gałęzi
   „czy cały akt się mieści".
3. **Wszystko, co idzie do promptu, jest NUMEROWANYM źródłem.** Wariant „8 źródeł + sąsiedztwo jako
   nienumerowane tło" jest PUŁAPKĄ: model znajduje próg w tle, cytuje „art. 12",
   `CitationValidator` nie znajduje go w `contextTexts` i **oznacza poprawną odpowiedź jako
   halucynację** — `AnswerGate` ją regeneruje albo odmawia. Sprawdzone: `GroundedPrompt.Build`
   numeruje pętlą po `chunks.Count`, a walidator dostaje `sources.Count` parametrem, więc utrzymanie
   `[n]` per artykuł jest **darmowe** i nie wymaga zmian w prompcie ani w walidatorze.
4. **Warunek koncentracji ogranicza zasięg zmiany.** Pytania, gdzie źródła są rozproszone po wielu
   dokumentach, zachowują się DOKŁADNIE jak dziś — nie puchnie każdy prompt.

**Precedens w repo:** most cytowań (`CitationBridgeAsync`) robi już dokładnie tę klasę operacji —
dociąga dodatkowe chunki PO rankingu, po metadanych, bez embeddingu. Kopiujemy wzorzec.

**Infrastruktura jest gotowa:** `ChunkEntity` ma unikalny indeks `(DocumentId, ChunkIndex)`
(`PrawoRagDbContext.cs:90`), więc pobranie sąsiadów to zapytanie po zakresie na indeksie. `TokenCount`
per chunk pozwala pilnować budżetu bez tokenizacji.

---

## Zadanie 1 ✅ ZROBIONE: ArticleNeighbourhood — czysta funkcja wyboru zakresów

**Pliki:**
- Create: `src/PrawoRAG.Domain/Retrieval/ArticleNeighbourhood.cs`
- Test: `tests/PrawoRAG.Tests/Retrieval/ArticleNeighbourhoodTests.cs` (nowy)

**Interfejsy:**
- Produces: `ArticleNeighbourhood.Plan(IReadOnlyList<RetrievedChunk> final, int minChunks, int radius)`
  → `IReadOnlyList<(Guid DocumentId, int FromIndex, int ToIndex)>`.
- Czysta funkcja, zero I/O — cała arytmetyka zakresów testowalna bez bazy.
- Wejście: finalna lista. Wyjście: scalone zakresy `ChunkIndex` per dokument (nakładające się
  przedziały łączone, żeby nie pobierać tego samego dwa razy).
- Kwalifikują się WYŁĄCZNIE dokumenty o `DocType == DocTypes.Act` z liczbą chunków w `final`
  ≥ `minChunks`. Orzeczenia świadomie pominięte: ich segment to długie uzasadnienie, sąsiedztwo
  nie ma tam struktury artykułowej, a pełny tekst wyroku to narracja, nie przepisy.

- [ ] Testy: 8 trafień rozsianych po akcie ⇒ zakresy scalone w jeden/kilka; jedno trafienie
      w dokumencie ⇒ dokument NIE kwalifikuje się (poniżej `minChunks`); orzeczenia pomijane
      niezależnie od liczby trafień; `radius = 0` ⇒ pusta lista (wyłącznik); zakresy przycięte
      od dołu do 0 (trafienie w `ChunkIndex = 0` nie generuje ujemnego indeksu); dwa dokumenty
      kwalifikujące się jednocześnie ⇒ dwa niezależne zestawy zakresów.
- [ ] Implementacja.
- [ ] Commit: `feat(retrieval): ArticleNeighbourhood - plan zakresow sasiedztwa dla dominujacych aktow`

## Zadanie 2 ✅ ZROBIONE: dociągnięcie sąsiadów w HybridRetriever

**Pliki:**
- Modify: `src/PrawoRAG.Domain/Retrieval/Retrieval.cs` (`RetrievalQuery`: `NeighbourhoodRadius`,
  `NeighbourhoodMinChunks`, `NeighbourhoodTokenBudget`)
- Modify: `src/PrawoRAG.Storage/Retrieval/HybridRetriever.cs` (po złożeniu `final`)
- Modify: `src/PrawoRAG.Api/Program.cs` + `appsettings.json` (`RetrievalOptions`)
- Test: `tests/PrawoRAG.Tests/Retrieval/ArticleNeighbourhoodLiveTests.cs` (nowy, kolekcja `LiveDb`)

**Interfejsy:**
- Nowe parametry zapytania, wzorem istniejącego `CitationBridgeArticles` (ten sam idiom: liczba = 0
  wyłącza mechanizm, więc `NeighbourhoodRadius = 0` ⇒ zachowanie bajt w bajt jak dziś).
- Etap raportowany jako `neighbourhood` przez `query.ReportStage` i `LatencyLog` — w TYM SAMYM
  punkcie, zgodnie z zasadą z Zadania 2 planu ROU (jedno źródło dla instrumentacji i UI).
- Dociągnięte chunki wchodzą do wyniku **po** `final`, posortowane po `(DocumentId, ChunkIndex)`,
  żeby akt czytał się liniowo. `Score` = `double.MinValue` (marker: przyszły sąsiedztwem, nie
  rankingiem — po tym testy i diagnostyka je poznają, analogicznie do `double.MaxValue` mostu).
- **Budżet tokenów** liczony z `TokenCount` na dociąganych chunkach; przekroczenie ⇒ ucinamy
  (najpierw najdalsze od trafień). To jest cała obsługa przypadku „kodeks" — bez osobnej gałęzi.
- `ExactMatchCap` NIE stosuje się do sąsiedztwa (cap istnieje po to, żeby jeden dokument nie zjadł
  budżetu TopK — a tutaj dominacja jednego aktu jest CELEM, nie awarią).

- [ ] Testy (`LiveDb`, zasiany akt o kilkunastu artykułach + orzeczenie): trafienia w art. 3 i 9
      ⇒ w wyniku są też sąsiednie artykuły; kolejność wynikowa rosnąca po `ChunkIndex`; budżet
      tokenów respektowany (mały budżet ⇒ mniej sąsiadów, nigdy więcej niż budżet); `Radius = 0`
      ⇒ wynik identyczny jak przed zmianą (test równoważności); orzeczenie z 8 trafieniami
      ⇒ zero dociągnięć; dokument z 2 trafieniami przy `MinChunks = 3` ⇒ zero dociągnięć.
- [ ] Implementacja.
- [ ] Commit: `feat(retrieval): sasiedztwo artykulow dla dominujacego aktu - budzet tokenow zamiast galezi na kodeks`

## Zadanie 3 ✅ ZROBIONE: panel źródeł grupowany po dokumencie

**Pliki:**
- Modify: `src/PrawoRAG.Api/Components/Pages/Chat.razor` (sekcja `Źródła (@ex.Sources.Count)`)
- Modify: `src/PrawoRAG.Api/wwwroot/css/app.css`

**Uzasadnienie:** to jedyny realny koszt tej zmiany. Przy rozszerzeniu sąsiedztwa panel może mieć
kilkadziesiąt kart zamiast 8 — nadal poprawnych i klikalnych (numeracja `[n]` działa bez zmian),
ale nieczytelnych. Grupujemy po dokumencie: nagłówek „Ustawa o … — N artykułów", trafienia
retrievalu widoczne od razu, sąsiedztwo zwinięte pod rozwijaniem.

- [ ] Grupowanie po `Title`/dokumencie z zachowaniem numeracji `[n]` (kotwice `#src-{AnchorId}-{n}`
      MUSZĄ działać dalej — na nich stoją klikalne cytowania).
- [ ] Odróżnienie wizualne: trafienie retrievalu vs dociągnięty sąsiad.
- [ ] Commit: `feat(ui): panel zrodel grupowany po dokumencie - czytelnosc przy rozszerzonym sasiedztwie`

## Zadanie 4 ✅ ZROBIONE: cytowania grupowane `[2, 3, 4]` — rozbicie na klikalne `[2] [3] [4]`

**Pliki:**
- Modify: `src/PrawoRAG.Api/Services/MarkdownRenderer.cs` (`CiteRe`, `DocCiteRe` i pętla podmiany)
- Modify: `src/PrawoRAG.Llm/Grounding/CitationValidator.cs` (`MarkerRegex`, `DocMarkerRegex`)
- Test: `tests/PrawoRAG.Tests/Grounding/AbstentionAndCitationTests.cs` (dopisać)
- Test: nowy plik dla renderera, jeśli go nie ma

**To NIE jest tylko problem UI.** Model czasem pisze `[2, 3, 4]` zamiast `[2] [3] [4]`, a oba miejsca,
które rozpoznają cytowania, wymagają cyfr **bezpośrednio** przed `]`:

- `MarkdownRenderer.CiteRe` = `\[(\d{1,2})\]` — grupa NIE jest linkowana, użytkownik widzi
  nieklikalny tekst;
- `CitationValidator.MarkerRegex` = `\[(\d+)\]` — grupa jest **niewidoczna dla bramki
  anty-fabrykacji**. Konsekwencje: `Cited` jest niekompletne (traci wartość diagnostyczną
  i zasila raport `--live-report` zaniżonymi liczbami), a cytat spoza zakresu **ukryty w grupie**
  (np. `[2, 99]` przy 8 źródłach) **nie zostanie wykryty** — `OutOfRange` go nie zobaczy, więc
  `AnswerGate` przepuści odpowiedź, która powołuje się na nieistniejące źródło.

Dlatego naprawa musi objąć OBA regexy, a nie tylko renderer.

**Decyzja: naprawiamy po stronie ODCZYTU, nie promptu.** Można by dopisać do `GroundedPrompt` regułę
„pisz `[2] [3] [4]`, nigdy `[2, 3, 4]`", ale (a) to prośba do modelu, nie gwarancja, (b) nie naprawia
**już zapisanych** odpowiedzi w historii, które UI renderuje ponownie po wczytaniu rozmowy.
Rozpoznawanie grup po stronie odczytu działa retroaktywnie i nie zależy od tego, czy model posłucha.

- [ ] `MarkerRegex`/`CiteRe` rozpoznają grupę: `\[\s*\d+(\s*,\s*\d+)*\s*\]` (i analogicznie `[D1, D2]`),
      a z niej wyciągane są WSZYSTKIE numery.
- [ ] Renderer emituje osobny link per numer (`[2] [3] [4]`, każdy z własną kotwicą
      `#src-{anchorId}-{n}`) — separator z oryginału (przecinek) zamieniony na spację, żeby nie
      renderować `[2], [3], [4]` z wiszącym przecinkiem poza linkiem.
- [ ] Walidator zlicza wszystkie numery z grupy do `Cited` i sprawdza każdy osobno przeciw
      `sourceCount` (`OutOfRange`).
- [ ] Testy walidatora: `[2, 3, 4]` ⇒ `Cited = [2,3,4]`; `[2, 99]` przy 8 źródłach ⇒ `99`
      w `OutOfRange` (dziś przechodzi niezauważone — to jest test regresji na realną dziurę);
      `[D1, D2]` w przestrzeni załącznika; pojedyncze `[2]` bez zmian; `[2,3]` bez spacji;
      tekst niebędący cytatem (np. `[2 marca]`) NIE łapany.
- [ ] Testy renderera: grupa daje trzy osobne `<a class="cite">`; numer spoza zakresu w grupie NIE
      jest linkowany (parytet z dzisiejszym zachowaniem dla pojedynczych cytatów).
- [ ] Commit: `fix(ui): cytowania grupowane [2, 3, 4] klikalne i widoczne dla bramki anty-fabrykacji`

---

## Weryfikacja — bez rytuału pomiarowego

Świadomie NIE opieramy decyzji na zamrożonym zestawie 30 pytań: **nie zawiera on przypadku „akt
trafiony, ale wybrane złe przepisy"**, więc pokazałby „bez zmian" i nie powiedziałby nic o klasie
problemu, która to wywołała.

**Sprawdzenie skutku (jedno pytanie):** zapytać o limity wpłat na OKI. Albo w źródłach jest przepis
o progu, albo nie ma. Bez evalu.

**Sprawdzenie regresji (to jest jedyny powód, żeby odpalić eval):** pytania, które dziś odpowiadają
dobrze, mają źródła rozproszone po wielu dokumentach, więc warunek koncentracji ich NIE łapie i ich
prompt się NIE zmienia. `--refusals` na zamrożonych 30 powinien dać wynik **identyczny**. Jeśli się
zmieni — warunek `MinChunksForExpansion` jest za luźny i widać to od razu po liczbie źródeł w panelu.

**Wartości startowe do dostrojenia po pierwszym uruchomieniu:** `NeighbourhoodRadius = 2`,
`NeighbourhoodMinChunks = 3`, `NeighbourhoodTokenBudget` ~20 000 (przy TopK=8 dzisiejszy kontekst to
~3,6 tys. tokenów, więc to ~5× więcej — dla ustawy o OKI oznacza praktycznie cały akt).

## Czego ten plan świadomie NIE robi

- Nie zmienia modelu embeddingu ani rozmiaru chunków (uzasadnienie na górze).
- Nie dodaje trybu „cały akt" jako osobnej gałęzi — budżet tokenów obsługuje kodeksy tym samym kodem.
- Nie rozszerza sąsiedztwa dla orzeczeń.
- Nie rusza `AbstentionThreshold` ani flag planu ROU.
