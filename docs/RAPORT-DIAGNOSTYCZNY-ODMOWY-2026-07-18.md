# Raport diagnostyczny — trzy realne odmowy czatu (2026-07-18)

Materiał do przekazania dalej. Trzy niezależne, realne pytania użytkownika zakończone „Nie mam
wystarczających źródeł, aby odpowiedzieć." Dane wyciągnięte bezpośrednio z tabeli `messages` na
produkcyjnej bazie (192.168.100.11), model `gemma4:26b-mlx`, reranker włączony (Bielik: `sdadas/polish-reranker-roberta-v3`).

## Zastrzeżenie metodologiczne

`MessageEntity.RetrievedSources` (jsonb) trzyma źródła korpusowe podane do generacji. **Fragmenty
załączonego dokumentu (DOC) NIE są persystowane** — `Chat.razor:408-409` zapisuje tylko `ex.Sources`
(`SourcesEvent`), `ex.DocFragments` (`DocSourcesEvent`) zostaje wyłącznie w stanie UI i ginie z sesją.
Dla przypadku z załącznikiem (case 2) nie da się więc retrospektywnie sprawdzić, jakie fragmenty umowy
faktycznie trafiły do modelu — tylko że korpusowa część promptu przeszła bramkę.

**Kluczowe odkrycie wstępne, wspólne dla wszystkich trzech przypadków:** `Abstained = false` w bazie,
mimo treści „Nie mam wystarczających źródeł". To znaczy: bramka progu podobieństwa (`AbstentionPolicy.
ShouldAbstain`, `MaxSimilarity`) **przepuściła** za każdym razem — źródła (8, 11, 17) zostały pobrane
i podane modelowi. To **sam model** (`GroundedPrompt.SystemPrompt`, reguła 3) ocenił dostarczone źródła
jako nietrafne i odmówił na poziomie treści. To odmienny mechanizm od diagnozy z 2026-07-17 (tam problem
był w tym, że norma w ogóle nie wchodziła do puli kandydatów — tu norma/przepis **wchodzi**, ale albo
jest rozcieńczona zanieczyszczeniem, albo model jej nie rozpoznaje jako wystarczającej).

---

## Case 1 — Dzierżawa nieruchomości w trwałym zarządzie (follow-up)

**Wątek** (3 tury, follow-up na follow-upie):
1. *„Jakie zgody są potrzebne do oddania w dzierżawę nieruchomości którą jednostka budżetowa dysponuje
   w ramach trwałego zarządu?"* → **odpowiedź dobra** (20 źródeł, cytuje [1], poprawnie referuje UGN
   art. 60 ust. 1 pkt 1).
2. *„Kto jest organem właściwym lub nadzorującym?"* → **odpowiedź dobra** (11 źródeł, poprawnie
   rozróżnia UGN vs ustawę o zasadach zarządzania mieniem państwowym, cytuje [2][3][5][6]).
3. *„kto to będzie w przypadku gdy nieruchomością dysponuje jednostka budżetowa która ma przekazaną
   nieruchomość od jednostki samorządu terytorialnego - gminy?"* → **ODMOWA** (11 źródeł podanych).

### Źródła podane przy odmowie (tura 3)
```
KPA art. 5 § 1, art. 5 § 2, art. 268a                          — definicje ogólne, NIE o mieniu/zarządzie
UGN art. 43                                                     — TRAFNE (trwały zarząd)
UGN art. 48                                                     — prawdopodobnie trafne (sąsiedni przepis)
Ustawa o związku metropolitalnym w woj. pomorskim, art. 43      — ZANIECZYSZCZENIE (patrz niżej)
Ustawa o związku metropolitalnym w woj. pomorskim, art. 48      — ZANIECZYSZCZENIE
Ustawa o związku metropolitalnym w woj. pomorskim, art. 7a      — ZANIECZYSZCZENIE
Sąd Okręgowy w Elblągu, I Ca 347/14 (2014)
III CZP 35/04 (2004) — zduplikowane (2×)
```

### Obserwacja
Trafna norma (UGN art. 43/48) **jest** w źródłach — ale rozcieńczona: (a) ogólnymi definicjami z KPA
niewnoszącymi nic do pytania, (b) artykułami o TYCH SAMYCH numerach (43, 48, 7a) z zupełnie niezwiązanej
ustawy o związku metropolitalnym w woj. pomorskim (2026). Pytanie użytkownika NIE zawiera cytatu „art.
43" — więc to nie tor strukturalny (QU-3) na treści pytania.

**Mechanizm — NIEROZSTRZYGNIĘTY, dwaj kandydaci (nie wiadomo który, wymaga sprawdzenia na 3060):**
1. *Most cytowań* (`CitationBridgeAsync`) mógłby dociągać artykuł po samym numerze z cytowań w
   trafionych orzeczeniach — ale `ResolveActAsync` mostu rozpoznaje akty po krótkich aliasach (kc, kpc,
   …); bespoke ustawa z 2026 r. o związku metropolitalnym prawdopodobnie TAKIEGO aliasu nie ma, więc
   most raczej nie powinien jej dociągnąć. Do potwierdzenia empirycznie.
2. *Zwykłe rozproszone dopasowanie gęste* — to szeroki akt administracyjny (związek metropolitalny
   dotyka wielu tematów: mienia, wspólnej obsługi, zadań publicznych), którego liczne artykuły mogą
   embedować się generycznie blisko wielu zapytań samorządowych. Ten mechanizm lepiej pasuje do wzorca
   z Case 3 (patrz niżej — tam ten sam akt pojawia się z ROZPROSZONYMI numerami 16c/12/15/64/81/18/12b/7a,
   nie z numerami odpowiadającymi trafnemu przepisowi, co przeczy dopasowaniu „po numerze z cytowania").

Nie da się dziś potwierdzić, który mechanizm to faktycznie jest bez wglądu w bazę na 3060 (które
orzeczenie/query dociągnęło ten akt). Sama OBSERWACJA — że ten konkretny akt jest powtarzalnym
„atraktorem" w dwóch niezwiązanych pytaniach — jest solidna i warta zbadania niezależnie od mechanizmu.

**Możliwa przyczyna niepowodzenia modelu:** nawet mając UGN art. 43 w kontekście, model mógł nie
rozpoznać w NIM konkretnego przepisu odpowiadającego na pytanie „gmina jako przekazujący" (subtelna
różnica: Skarb Państwa vs jednostka samorządu terytorialnego jako źródło trwałego zarządu — może wymagać
innego ustępu niż ten, który trafił do kontekstu jako chunk).

---

## Case 2 — Umowa o świadczenie usług B2B (załącznik PDF)

**Korekta etykiety:** dokument NIE jest „umową o dzieło" (mylnie nazwana tak we wcześniejszym opisie
rozmowy) — to „Umowa o świadczenie usług" między dwoma przedsiębiorcami (Zleceniodawca/Kontraktor),
bliższa konstrukcyjnie umowie zlecenia (KC art. 734 i nast.) niż umowie o dzieło. Ustalone dopiero przy
reprodukcji — patrz niżej.

**Pytanie** (zadane 3×, w tym powtórka „jeszcze raz" tego samego dnia i ponownie 2h później —
deterministyczne, `Temperature=0`, za każdym razem identyczny wynik): *„Z perspektywy kontraktora czy ta
umowa wystarczająco chroni jego interes?"*

**Wynik:** ODMOWA za każdym razem, 8 źródeł korpusowych podanych (fragmenty umowy — nieznane, nie
persystowane, patrz zastrzeżenie metodologiczne wyżej).

### Źródła korpusowe (8, ostatnia próba)
```
Sąd Apelacyjny w Gdańsku, I ACa 910/15 (2016) — zduplikowane (2×)
Sąd Apelacyjny w Katowicach, I ACa 1476/22 (2025)
Sąd Okręgowy w Szczecinie, II Ca 1178/14 (2015)
Sąd Rejonowy dla Warszawy-Śródmieścia, VI C 1124/20 (2021)
Sąd Apelacyjny we Wrocławiu, I ACa 1135/15 (2015)
Sąd Rejonowy w Słupsku, VI GC 162/14 (2015)
Sąd Okręgowy w Lublinie, I C 22/13 (2014)
```
Same tytuły nie mówią wprost, czy to sprawy o umowę o dzieło — wymaga sprawdzenia treści.

### Reprodukcja z `PRAWORAG_DUMP_PROMPT` (2026-07-18, ten sam PDF, to samo pytanie, reranker OFF —
niedostępny w chwili testu, nie wpływa na dobór fragmentów dokumentu, patrz niżej)

Pełny prompt wysłany do modelu odtworzony. Kluczowe ustalenia — **zastępują obie hipotezy z draftu**:

1. **Dobór fragmentów dokumentu jest POPRAWNY.** [D1]-[D3] pokrywają całą treść umowy sekwencyjnie
   (§1 Przedmiot → §2-3 Wynagrodzenie → §3 Czas trwania → §4 Postanowienia końcowe), bez ucięć w środku
   klauzuli. Hipoteza 1 (zły dobór fragmentów) — **obalona**.
2. **Struktura promptu jest poprawna** — sekcje DOKUMENT i ŹRÓDŁA są jasno rozdzielone, reguły D1-D4
   jednoznacznie każą czerpać fakty z DOKUMENTU a prawo z ŹRÓDEŁ. Hipoteza 2 (myląca struktura) — brak
   podstaw w treści promptu, **nieprawdopodobna**.
3. **Prawdziwe znalezisko: wśród 8 podanych ŹRÓDEŁ nie ma ANI JEDNEGO przepisu (PRZEPISY) — same
   orzeczenia, i to słabo trafione:**
   - [1] i [2] to **duplikat tego samego wyroku** (ten sam sygn. akt, ten sam fragment, rozdzielone
     tylko znakiem „⚫").
   - [8] (Sąd Okręgowy w Poznaniu, III K 93/17) to **sprawa KARNA** o wiarygodności donosiciela —
     zero związku merytorycznego z prawem umów; trafiło najpewniej przez powierzchniowe podobieństwo
     frazy o „przerzucaniu odpowiedzialności".
   - [5] (Sąd Okręgowy w Krakowie, VII U 3928/19) to sprawa **ubezpieczeniowa/adaptacji do nowej
     regulacji** — również luźno związana.
   - Pozostałe ([3][4][6][7]) dotykają tematu (kara umowna, odstąpienie, nienależyte wykonanie), ale
     każde to pojedyncze, wyrwane z kontekstu zdanie, nie fragment niosący samodzielną treść.
   - **Nigdzie nie pojawia się KC art. 734 i nast. (zlecenie/świadczenie usług)** ani żaden inny przepis
     rządzący tym typem umowy.
4. **Wniosek zmienia ramę Case 2:** system prompt (reguła 3: odmów, gdy źródła nie zawierają
   odpowiedzi; reguła 4: nie wymyślaj przepisów) **każe** odmówić, gdy model nie dostał ani jednego
   przepisu. Odmowa modelu jest tu w dużej mierze **zgodna z instrukcją**, nie awarią samego modelu —
   prawdziwy problem leży wyżej, w retrievalu korpusowym: dla pytania **oceniającego, bez słownictwa
   prawniczego** („czy ta umowa wystarczająco chroni jego interes?" — cała faktografia jest w
   załączniku, nie w pytaniu) nie udało się dowieźć ŻADNEGO trafnego przepisu, tylko przypadkowy zestaw
   orzeczeń.

---

## Case 3 — Centrum Usług Wspólnych (rozliczenia międzygminne)

**Pytanie:** *„Czy jest możliwe przekazania Centrum Usług Wspólnych zadania polegającego na dokonywaniu
rozliczeń międzygminnych?"* (zadane 2×, druga próba to powtórka po pustej odpowiedzi).

**Próba 1 (17:26:03) — PUSTA ODPOWIEDŹ, pusty `Model`.** Retrieval zdążył zapisać 17 źródeł, ale
generacja nigdy nie doszła do końca — wskazuje na wyjątek wyrzucony w trakcie/przed generacją (pusty
`Model` = `DoneEvent` nigdy nie nadszedł normalnie).

**NIEROZSTRZYGNIĘTE, czy to crash rerankera.** Wygląda podobnie do sygnatury `TeiReranker` 422
(„batch size >32", naprawione tego samego dnia w `c30f57d`) — ale dane przeczą temu jednoznacznie:
**próba 2, to samo zapytanie 5 minut później, z tymi samymi 17 źródłami (retrieval deterministyczny),
NIE crashuje** — zwraca normalną odmowę treściową. Crash 422 rerankera jest deterministyczny względem
liczby kandydatów; to samo zapytanie powinno crashować identycznie za drugim razem, jeśli przyczyną
byłby rozmiar batcha. To, że nie crashuje, jest dowodem PRZECIW hipotezie rerankera, nie za nią.
Alternatywna hipoteza, nieprzetestowana: `gemma4:26b-mlx` jest wolna (zmierzone ~82s na krótszym
prompcie w tej samej sesji) — pełny prompt RAG mógł przekroczyć timeout HTTP/klienta i zostać przerwany
w trakcie generacji, dając pustą treść i pusty `Model` bez udziału rerankera. Nie da się dziś ustalić,
czy reranker był w ogóle włączony o 17:26 (brak logu konfiguracji w danych, którymi dysponuję) — to
samo w sobie jest lukę do zamknięcia, nie fakt do założenia.

**Próba 2 (17:31:29) — ODMOWA**, 17 źródeł.

### Źródła (17, wspólne dla obu prób — retrieval deterministyczny)
```
Ustawa o samorządzie gminnym, art. 10c                          — TRAFNE (wspólna obsługa/CUW)
Ustawa zmieniająca usg, art. 10c, art. 8e, art. 21               — TRAFNE (nowelizacja wprowadzająca CUW)
Ustawa o związku metropolitalnym w woj. pomorskim: art. 16c, 12,
  15, 64, 81, 18, 12b, 7a (8 różnych artykułów!)                 — ZANIECZYSZCZENIE, jak w Case 1
Ustawa o realizowaniu usług SPOŁECZNYCH przez centrum usług
  społecznych, art. 15, art. 12                                  — MYLĄCE (CUS ≠ CUW, patrz niżej)
KIO 2544/12 (2012)
```

### Obserwacje — DWA odrębne wzorce zanieczyszczenia
1. **Pułapka nazewnicza CUS vs CUW.** „Centrum Usług Społecznych" (ustawa z 2019 r., zupełnie inna
   instytucja — usługi społeczne dla mieszkańców) i „Centrum Usług Wspólnych" (wspólna obsługa
   jednostek organizacyjnych JST, usg art. 10a-10c) to **różne, leksykalnie bliskie** pojęcia. To ten
   sam rodzaj pułapki co art. 149 vs art. 415 KC z wczorajszej diagnozy (podobieństwo powierzchniowe,
   różna treść merytoryczna) — tylko na poziomie NAZW INSTYTUCJI, nie numerów artykułów.
2. **Ten sam „atraktor" co w Case 1**: ustawa o związku metropolitalnym w woj. pomorskim (2026) —
   **8 różnych artykułów** w źródłach dla pytania, które nie ma NIC wspólnego z metropoliami. W
   połączeniu z Case 1 (3 artykuły tej samej ustawy, też niezwiązane tematycznie) to silny sygnał, że
   ten konkretny akt jest **systemowo nadreprezentowany** w retrievalu — wart osobnego zbadania: czy to
   przez most cytowań (dopasowanie po numerze artykułu bez potwierdzenia aktu), czy przez jakość
   embeddingu tego konkretnego, świeżo zaindeksowanego dokumentu (podobnie jak zdegenerowane wektory
   `REGULATION` z wcześniejszej diagnozy — może ten akt ma nietypowo „centralne"/ogólne wektory).

---

## Case 4 — KSeF (akronim vs pełna nazwa w korpusie)

**Nie z bazy produkcyjnej** — test interaktywny, 2026-07-19, ten sam pipeline (bez rerankera, TEI na
3060 nieodpowiadał w chwili testu, patrz otwarte pytanie o Case 3).

**Pytanie:** *„Kogo obejmuje obowiązkowy KSEF w 2026 i co oznacza okres przejściowy?"* → **ODMOWA**,
8 źródeł.

### Źródła (8)
```
PRZEPISY (3) — WSZYSTKIE puste placeholdery „(pominięty)"/„(pominięte)":
  Ustawa o zakładowym funduszu świadczeń socjalnych, art. 16
  Ustawa o pracownikach samorządowych, art. 58
  Ustawa o emeryturach kapitałowych, art. 34-36
ORZECZNICTWO (5) — wszystkie o OBOWIĄZKOWYCH UBEZPIECZENIACH SPOŁECZNYCH (ZUS), zero związku z VAT/KSeF
```

### Diagnoza — zweryfikowana bezpośrednio przez `/api/search` (nie tylko wnioskowanie ze źródeł)

1. **Materiał o KSeF ISTNIEJE w korpusie i jest dobrze zaembedowany.** Zapytanie dosłowne *„Krajowy
   System e-Faktur"* trafia bezbłędnie: art. 106nd ustawy o VAT (definicja KSeF, similarity 0,85) i
   art. 4 nowelizacji z 2021 r. („Tworzy się Krajowy System e-Faktur", similarity 0,86). Korpus ma też
   dedykowane rozporządzenie *„w sprawie korzystania z Krajowego Systemu e-Faktur"* (wersje z grudnia
   2025 i lutego 2026) oraz dziesiątki nowelizacji ustawy o VAT z 2025-2026.
2. **Ale korpus prawie nigdzie nie używa SKRÓTU „KSeF"/„KSEF"** — tylko **21 chunków w całym korpusie**
   (zapytanie `SELECT count(*) FROM chunks WHERE "Text" ILIKE '%Krajowy System e-Faktur%' OR "Text"
   ILIKE '%KSeF%'`) zawiera którąkolwiek z tych fraz; ustawy konsekwentnie piszą pełną nazwą.
3. **Parafraza z akronimem też zawodzi**: zapytanie *„obowiązkowe fakturowanie ustrukturyzowane KSeF"*
   (wciąż zawiera „KSeF") zwraca same przypadkowe wzmianki „faktura VAT" z list dowodowych w sprawach
   karnych — embedding CAŁEGO zapytania ucieka od tych 21 chunków, gdy dominują je frazy generyczne
   („obowiązkowe", „kogo obejmuje", „okres przejściowy").
4. **Wniosek:** to CZWARTY odrębny mechanizm odmowy w tej diagnostyce (obok: dylucji precyzją Case 1/3,
   deficytu recall dla pytań oceniających Case 2, i binarnej reguły odmowy przy pytaniach złożonych —
   patrz przypadek ze spadkiem, do dopisania osobno) — i najbardziej wprost naprawialny: **model
   embeddingu nie generalizuje akronimu prawnego na jego pełną nazwę**, więc zapytanie żargonem
   branżowym (typowe dla użytkownika, który zna skrót, ale nie zna dokładnego brzmienia ustawy) przegrywa
   z generyczną otoczką prawną innych, niezwiązanych przepisów. Możliwe kierunki naprawy (niewdrożone,
   do dyskusji): rozszerzanie znanych akronimów prawnych (KSeF, RODO, KPA, KRS...) na pełną nazwę przed
   embeddingiem zapytania; silniejsza waga BM25/dokładnego dopasowania tekstu, gdy zapytanie zawiera
   ciąg caps-lock przypominający skrót.

### Skala problemu „(pominięty)" — zmierzona
Ten sam wzorzec pustych placeholderów co w teście załącznika (`docs/TEST-ZALACZNIK-UMOWA-NAJMU.md`,
zapytanie o kaucję) pojawił się tu jako WSZYSTKIE 3 wyniki PRZEPISY. Zmierzone bezpośrednio w bazie:
**1056 z 544 805 chunków aktów (≈0,19%)** ma tekst pasujący do `(pominięty)`/`(pominięte)`. Niewielki
odsetek globalnie, ale systemowo podatny na trafienie właśnie przy zapytaniach o „przepisy przejściowe"/
„co się zmienia" — bo rozdziały „Przepisy przejściowe i końcowe" nieproporcjonalnie często zawierają
uchylone/pominięte numery artykułów. Kandydat na filtr analogiczny do wcześniejszego `REGULATION`
(wykluczyć chunki, których cała treść to placeholder uchylenia, zamiast serwować pustkę jako źródło).

---

## Case 5 — Najniższa cena z 30 dni (dyrektywa Omnibus) — NAJPOWAŻNIEJSZE znalezisko sesji

**Nie z bazy produkcyjnej** — test interaktywny, 2026-07-19, realna rozmowa w UI (nie tylko `/api/search`).

**Pytanie:** *„Jak prawidłowo oznaczyć najniższą cenę z ostatnich 30 dni? Kto jest zobowiązany do
oznaczania?"* → **ODMOWA**, 8 źródeł.

### Źródła (8, z realnej rozmowy)
```
[1] Ustawa o informowaniu o cenach towarów i usług, art. 9-21 — PUSTY placeholder „(pominięte)"
[2] Rozporządzenie MRiT 19.12.2022 ws. uwidaczniania cen, § 1 pkt 1 — wyliczenie, nie norma materialna
[3] Rozporządzenie MRiT 19.12.2022 ws. uwidaczniania cen, § 3 pkt 2 — wyliczenie („w cenniku;")
[4] Sąd Rejonowy w Nowym Dworze Mazowieckim, II K 13/15 — ŚMIECI: „(...) roku, (...) z dnia (...)" ×10
[5] Sąd Apelacyjny we Wrocławiu, II AKa 156/14 — zakup narkotyków, zero związku
[6] Sąd Apelacyjny w Poznaniu, II AKa 91/19 — ŚMIECI: „kontrola operacyjna (...) pod kryptonimem (...)"
[7] Sąd Apelacyjny w Krakowie, I AGa 328/21 — terminy realizacji zadań budowlanych, zero związku
[8] Sąd Apelacyjny w Poznaniu, II AKa 91/19 — DUPLIKAT [6]
```

### Diagnoza — zweryfikowana bezpośrednio w bazie (nie tylko wnioskowanie ze źródeł)

**Poprawna, kompletna odpowiedź ISTNIEJE w korpusie jako jeden, prawidłowo zaembedowany chunk** —
art. 4 ustawy o informowaniu o cenach towarów i usług (aktualny tekst, „w brzmieniu ustalonym przez
art. 6 pkt 2" noweli — czyli to WŁAŚCIWA, zaktualizowana wersja implementująca dyrektywę Omnibus):
ust. 1 kto jest zobowiązany (sprzedaż detaliczna/usługi), ust. 2 dosłownie reguła 30 dni, ust. 3
wariant dla ofert <30 dni, ust. 4-6 wyjątki i delegacja. Zweryfikowano bezpośrednio w bazie: chunk ma
`EmbeddedWith='sdadas/mmlw-retrieval-roberta-large-v2'` (właściwy, aktualny model), `Embedding` NIE
jest pusty, 402 tokeny.

**A mimo to ten chunk nie pojawia się WCALE wśród źródeł** — ani w realnej rozmowie, ani w bezpośrednim
teście `/api/search` z zapytaniem niemal dosłownie cytującym ust. 2 („najniższa cena w okresie 30 dni
przed wprowadzeniem obniżki" — realny tekst: „najniższej cenie... która obowiązywała w okresie 30 dni
przed wprowadzeniem obniżki"). Prawie-dosłowny cytat przepisu i tak przegrywa z niezwiązanymi wynikami.

**Dwa kompletnie odrębne mechanizmy zanieczyszczenia, oba w jednym zestawie źródeł:**
1. **`(pominięty)` placeholder** (art. 9-21) — ten sam wzorzec co w Case 4, tu na dodatek konkretnie
   sąsiaduje numerycznie z właściwym art. 4 (ten sam akt), więc mógł „przyciągnąć" trafienie kosztem
   właściwego artykułu w tej samej ustawie.
2. **NOWY wzorzec — placeholdery anonimizacji SAOS.** `[4]`, `[6]`, `[8]` to nie merytoryczny szum,
   tylko powtórzone frazy anonimizacyjne („(...) roku, (...) z dnia (...)" ×10, „kontrola operacyjna
   (...) pod kryptonimem: (...)" ×3) z zupełnie niezwiązanych spraw karnych. Krótkie, silnie
   powtarzalne teksty zdominowane wzorcem dat/okresów — dokładnie ten sam mechanizm co przy filtrze
   `REGULATION` z wcześniejszej diagnozy (krótkie, prawie identyczne teksty tworzą sztucznie „lepki"
   klaster w przestrzeni embeddingów), tylko inne źródło śmieci: nie typ dokumentu, tylko artefakt
   anonimizacji danych osobowych w tekstach orzeczeń.

**ROZSTRZYGNIĘTE (JAK-3, sonda `--probe-chunk`, 2026-07-19):** hipoteza o rozmyciu chunka (6 podtematów
w 402 tokenach) — **obalona**. Sonda na art. 4 vs to pytanie:
```
A. exact fp32:   pozycja #41     (sim=0,7913) — z ~7,4M chunków; sensowne, nie zdegenerowane
B. exact fp16:   pozycja #41     (zgodne z fp32 — brak straty od kwantyzacji halfvec)
C. HNSW (ef=400): pozycja #33    (zgodne z A/B — brak straty recall indeksu)
D. BM25:          NIEOBECNY — tsquery nie matchuje (AND wszystkich słów, jak w Case 4)
E. fuzja RRF:     dense@50 → #33, pula do dedupu = TopK×4 = 32 → ODPADA, jedno miejsce ZA odcięciem
```
**Prawdziwy mechanizm: twardy próg kandydatów (32) o włos za wąski**, nie jakość embeddingu ani
indeksu. Chunk semantycznie trafiony (#33-41 z milionów to dobry wynik), ale konkurenci zajmują górne
pozycje w tym samym 32-elementowym oknie, wypychając go poza próg PRZED dedupem — nigdy nie dociera
do promptu.

**Weryfikacja po JAK-1 (`--sanitize-chunks --apply`, 5449 chunków wyzerowanych, 2026-07-19):**
powtórka sondy — ranking POPRAWIŁ SIĘ tylko nieznacznie (fp32 #41→#40), a **HNSW zostało dokładnie na
#33 — wciąż jedno miejsce za progiem 32**. Wniosek: hipoteza „usunięcie śmieci wystarczy" — **częściowo
obalona** dla TEGO konkretnego przypadku. Chunki zajmujące górne ~33 pozycje dla tego zapytania to NIE
są zdegenerowane placeholdery/anonimizacja (te już usunięte) — to inne, prawdziwe akty/rozporządzenia,
tylko merytorycznie niewłaściwe (ten sam wzorzec „przepisy przejściowe"/leksykalna bliskość co gdzie
indziej w raporcie). JAK-1 pomaga ogólnie w korpusie, ale nie zamyka akurat tej granicznej luki.
**Najbardziej chirurgiczny, zmierzeniem uzasadniony następny krok: poszerzyć pulę przed dedupem**
(dziś `TopK×4=32` w `HybridRetriever`) — art. 4 stabilnie ląduje w okolicy #33-41 we wszystkich trzech
metodach pomiaru (fp32/fp16/HNSW), więc np. `TopK×6` czy `TopK×8` powinno go złapać bez większego
narzutu obliczeniowego.

**Dlaczego to najpoważniejsze znalezisko sesji:** to pierwszy w całej dzisiejszej diagnostyce przypadek,
gdzie odpowiedź jest 100% obecna, kompletna i prawidłowo zaindeksowana — a mimo to retrieval jej NIE
ZNAJDUJE nawet dla zapytania niemal cytującego przepis. Poprzednie przypadki (Case 1-4) miały choć
częściowe wytłumaczenie (dylucja, brak recall, akronim, złożone pytanie) — tu zawodzi rdzeń mechanizmu
dopasowania semantycznego przy współwystępowaniu śmieciowych chunków.

---

## Case 6 — Odrzucenie spadku bez zachowania kolejności (pytanie złożone, 4 podpytania)

**Nie z bazy produkcyjnej** — test interaktywny, 2026-07-19, zwykła rozmowa BEZ załącznika.

**Pytanie** (parafraza, oryginał dłuższy i bardziej opisowy): *„Odrzuciłem u notariusza spadek nie
zachowując kolejności odrzucania (notariusz mnie o tym nie poinformował). Dwójka moich dzieci odrzuciła
spadek w terminie, ale syn nie złożył oświadczenia i twierdzi, że moje odrzucenie się nie liczy, bo
kolejność nie została zachowana. Czy moje odrzucenie się liczy? Czy pozostałe dzieci muszą jeszcze raz
odrzucać? Jeśli anuluję odrzucenie, czy będę automatycznie obciążony długami? Co powinien zrobić syn,
jeśli anulowanie jest niemożliwe?"* → **ODMOWA**, mimo dobrego retrievalu.

### Źródła (8, zrzut promptu)
```
PRZEPISY (5) — WSZYSTKIE realnie na temat, bez zanieczyszczeń:
  KC art. 1019 § 1-3 — uchylenie się od skutków oświadczenia (błąd/groźba), wymaga zatwierdzenia sądu
  KC art. 1015 § 1-2 — termin 6 mies.; brak oświadczenia = przyjęcie z dobrodziejstwem inwentarza
ORZECZNICTWO (3) — opisy podobnych spraw (sekwencyjne odrzucanie przez pokolenia), bez wyraźnej tezy
  rozstrzygającej akurat kwestię „kolejności"
```

### Diagnoza — inny mechanizm niż Case 1-5

**Retrieval zadziałał dobrze** — 5 przepisów KC, właściwa gałąź prawa (spadkowe), bez zanieczyszczeń
klasy „wadium"/„związek metropolitalny"/placeholderów. To odróżnia ten przypadek od wszystkich
poprzednich.

**Ale premisa syna („trzeba zachować kolejność odrzucania") nie ma pokrycia w źródłach — ani
potwierdzenia, ani zaprzeczenia.** W dostarczonych przepisach nie ma takiego wymogu — cisza, nie
sprzeczność. Orzecznictwo [6]-[8] to tylko narracyjne opisy podobnych spraw, bez klarownej tezy.

**Prawdziwa przyczyna: pytanie ma 4 odrębne podpytania, a reguła 3 systemowego promptu jest binarna.**
Przynajmniej 2-3 z 4 podpytań dałoby się częściowo zaadresować z dostarczonych źródeł (mechanizm
uchylenia się przez sąd z art. 1019, termin i skutek milczenia z art. 1015) — ale `GroundedPrompt.cs`
nie ma trybu „częściowej odpowiedzi z zastrzeżeniem". Reguła 3: *„Jeśli dostarczone źródła NIE
zawierają odpowiedzi, napisz dokładnie: 'Nie mam wystarczających źródeł'"* — bez pośredniego wariantu
„na X mogę odpowiedzieć, na Y nie". Sprawdzone w kodzie ([GroundedPrompt.cs:41-42](src/PrawoRAG.Llm/Grounding/GroundedPrompt.cs)):
ta reguła jest DZIŚ, w bieżącym stanie repo, bajt w bajt identyczna jak przed wszystkimi dzisiejszymi
commitami — żaden z nich (`afab2ad`, `255db11`, `fc887d6`, `356d8fc`, `58e164e`) jej nie dotyka.

**To piąty, odrębny mechanizm zdiagnozowany w tej sesji** — nie problem precyzji retrievalu (Case 1/3),
nie recall (Case 2/5), nie generalizacji akronimu (Case 4), tylko **architektura promptu nieobsługująca
pytań wieloczęściowych**, gdzie poprawna odpowiedź wymagałaby rozbicia na osobne tezy z różnym
poziomem pewności, a obecna reguła wymusza całość-albo-nic.

---

## Podsumowanie — trzy odrębne, nowe wnioski (żaden nie pokrywa się z diagnozą z 2026-07-17)

1. **Bramka progu ≠ jakość odpowiedzi — ale mechanizm różni się między przypadkami.** We wszystkich
   trzech `Abstained=false`, ale model odmówił. W Case 1 i 3 trafna norma BYŁA w źródłach, tylko
   rozcieńczona zanieczyszczeniem (rzeczywisty problem precyzji). W Case 2 — po reprodukcji — okazało
   się, że wśród 8 źródeł nie było ŻADNEGO przepisu w ogóle, więc odmowa modelu tam jest zgodna z jego
   własną instrukcją (reguła 3/4); to nie deficyt precyzji tylko zwykły deficyt recall (podobny do
   diagnozy z 2026-07-17), tyle że dla pytania oceniającego bez słownictwa prawniczego.
2. **Nowy, powtarzalny „atraktor"**: ustawa o związku metropolitalnym w woj. pomorskim (2026) pojawia
   się z wieloma różnymi numerami artykułów w DWÓCH niezwiązanych tematycznie pytaniach. Wymaga
   zbadania czy to wina mostu cytowań (dopasowanie po numerze bez potwierdzenia aktu — ryzyko
   architektoniczne dodanej dziś funkcji) czy jakości/charakteru wektorów tego konkretnego dokumentu.
3. **Pułapka nazewnicza CUS/CUW** — instytucje o zbliżonych nazwach, różnej treści, myląco bliskie
   semantycznie/leksykalnie.
4. **Fragmenty dokumentu-załącznika nie są persystowane** — luka w obserwowalności produkcyjnej;
   Case 2 udało się mimo to rozstrzygnąć przez ręczną reprodukcję z `PRAWORAG_DUMP_PROMPT` (ten sam
   PDF, to samo pytanie, `Temperature=0` → deterministyczne), ale to obejście, nie naprawa luki — każdy
   kolejny przypadek z załącznikiem wymaga tej samej ręcznej reprodukcji.
5. **Pusta odpowiedź / crash na żywym ruchu produkcyjnym** (Case 3, próba 1) — realny wyjątek podczas
   generacji, nie tylko w testach syntetycznych. Przyczyna NIEROZSTRZYGNIĘTA: dane przeczą prostej
   hipotezie „to ten sam bug rerankera co naprawiliśmy w `c30f57d`" (identyczne zapytanie 5 min później
   nie crashuje ponownie) — patrz analiza w Case 3. Wymaga dalszego zbadania (logi z 17:26, czy reranker
   był wtedy włączony, czy to raczej timeout generacji Gemmy).

## Uzupełnienie (analiza kodu, 2026-07-18 wieczór) — atraktor to najpewniej `TemporalAugmenter`

Raport rozważał dwa mechanizmy (most cytowań vs rozproszone dopasowanie gęste) — analiza kodu wskazuje
TRZECI, który pasuje do wszystkich obserwacji naraz: **augmentacja nowelizacyjna (AKT-2)**.
`TemporalAugmenter` dla każdego aktu w wynikach bierze WSZYSTKIE chunki jego niewchłoniętych nowel
i dokłada te, których tekst WZMIANKUJE pytany numer artykułu (`\bart\. N\b`) — bez capu, ze
Score=MaxValue (czoło listy), POZA limitem TopK. Ustawa o związku metropolitalnym (2026) to świeża
ustawa z pakietem przepisów zmieniających — jeśli figuruje w `unabsorbedAmendments` UGN/usg/KPA,
jest skanowana przy każdym pytaniu trafiającym te akty.

Dopasowanie do dowodów raportu:
1. **11 i 17 źródeł przy TopK=8** — tylko augmenter dokłada poza TopK (most i QU są cięte w retrieverze);
   nadwyżka = dokładnie fragmenty metropolitalne + „ustawy zmieniającej usg".
2. **Case 1: metropolitalne art. 43/48 = numery trafionych UGN art. 43/48** — augmenter szukał wzmianek
   „art. 43"/„art. 48"; nagłówek „Art. 43." WŁASNEGO artykułu noweli też matchuje luźny regex.
3. **Case 3: rozproszone numery własne (16c, 12, 15…)** — raport uznał to za dowód przeciw dopasowaniu
   po numerze, ale w mechanice augmentera to oczekiwany wzorzec: numer WŁASNY chunka jest dowolny,
   liczy się WZMIANKA pytanych 10c/8e/21 w treści („o którym mowa w art. 10c ustawy o samorządzie
   gminnym…"). Osiem fragmentów = brak capu.

**Naprawa (commit z tą zmianą):** kontrakt AKT-2 to „fragmenty nowel DOTYCZĄCE pytanych artykułów" —
czyli ZMIENIAJĄCE przepis. `AmendmentDiffMatcher`: fragment kwalifikuje się tylko, gdy wzmianka numeru
współwystępuje z językiem diffu ZTP („otrzymuje brzmienie", „dodaje się", „uchyla się", „skreśla się",
„zastępuje się") — odsiewa odesłania i nagłówki własnych artykułów. Do tego capy: ≤2 chunki per nowela,
≤4 łącznie. Testy T-AKT-DIFF (9, w tym dosłowne wzorce z Case 1/3).

**Weryfikacja na M4/3060:**
1. `SELECT "Title", "TypedMetadata"->'unabsorbedAmendments' FROM documents WHERE "DocType"='act' AND
   "TypedMetadata"::text ILIKE '%metropolitaln%'` — potwierdzić, że ELI ustawy metropolitalnej figuruje
   jako nowela wielu aktów (dowód rozstrzygający mechanizm).
2. Powtórzyć pytania Case 1/3 — źródła bez fragmentów metropolitalnych (chyba że ta ustawa FAKTYCZNIE
   zmienia pytany artykuł — wtedy ma prawo być, w limicie 2).
3. Golden set kategoria Freshness (`fresh-kpc-nowela`) — realna nowela DU/2026/473 ma język diffu,
   musi nadal wchodzić (strażnik, że zaostrzenie nie wycięło legalnych nowel).

Zaostrzenie NIE adresuje: pułapki CUS/CUW (Case 3.1 — problem embeddingowy), doboru fragmentów
załącznika przy pytaniach oceniających (Case 2) ani pustej odpowiedzi (Case 3 próba 1) — te zostają
otwarte jak niżej.

## Otwarte pytania do dalszej analizy
- Czy „atraktor" (związek metropolitalny) to bug w moście cytowań, czy zwykłe rozproszone dopasowanie
  gęste (szeroki akt, generyczne embeddingi) — wymaga sprawdzenia na 3060 (który dokładnie
  mechanizm/orzeczenie dociągnęło te artykuły; wzorzec z Case 3 z rozproszonymi numerami artykułów
  raczej przemawia PRZECIW mostowi cytowań, ale to niepewne bez wglądu w dane).
- Co faktycznie spowodowało pustą odpowiedź w Case 3 próba 1 (17:26:03) — crash rerankera to jedna z
  kilku hipotez, osłabiona przez brak powtórki crasha przy identycznym zapytaniu 5 min później; wymaga
  logów serwera z tego okna czasowego i potwierdzenia, czy reranker był wtedy w ogóle włączony.
- ~~Reprodukcja Case 2 z `PRAWORAG_DUMP_PROMPT`~~ — zrobione, patrz Case 2: dobór fragmentów OK,
  problem w retrievalu (zero przepisów w źródłach). Otwarte pozostaje: DLACZEGO retrieval nie znalazł
  KC art. 734+ dla tego pytania — wymaga sprawdzenia czy taki chunk w ogóle jest w indeksie/czy
  embedding pytania „czy ta umowa chroni interes" jest zbyt odległy od embeddingu przepisu o zleceniu.
- Reranker TEI na 3060 (port 8081) nie odpowiadał podczas reprodukcji (timeout 75s) — do sprawdzenia,
  czy to trwały stan (kontener padł) czy przejściowe; ten sam kształt awarii (wyjątek w trakcie
  generacji → pusta odpowiedź, pusty `Model`) pasuje też do niewyjaśnionej pustej odpowiedzi w Case 3.
- Czy warto rozróżniać w UI/logice „odmowa progu" (Abstained=true) od „odmowa treściowa modelu"
  (RefusalMarker w Content, Abstained=false) w samej bazie/telemetrii — dziś nieodróżnialne bez
  parsowania treści, co utrudniało tę analizę.
- ~~[Case 5] Zmierzyć ranking art. 4 vs próg TopK~~ — zrobione sondą `--probe-chunk` (JAK-3):
  #33-41 z 7,4M (sensowne), pada na progu fuzji TopK×4=32 o jedno miejsce. Patrz rozstrzygnięcie w
  Case 5 wyżej.
- **[Case 5]** Zmierzyć skalę „placeholderów anonimizacji SAOS" (`(...) roku, (...) z dnia (...)`,
  `kontrola operacyjna (...) pod kryptonimem (...)` i podobne wzorce) analogicznie do pomiaru
  `(pominięty)` — ile takich chunków jest w korpusie, i czy kwalifikują się do tego samego typu filtra
  co `REGULATION`/`(pominięty)` (chunk zdominowany powtórzonym tokenem anonimizacji = brak wartości
  informacyjnej, bezwarunkowo wykluczyć z retrievalu).
- **[Case 6]** Czy `GroundedPrompt.SystemPrompt` powinien dostać tryb odpowiedzi częściowej („na X
  odpowiadam z [n], na Y źródła milczą") zamiast dzisiejszej binarnej reguły 3 — decyzja produktowa
  (zmiana zachowania na WSZYSTKICH pytaniach, nie tylko złożonych), wymaga świadomego wyboru, nie tylko
  fixu technicznego; ryzyko: częściowe odpowiedzi mogą obniżyć postrzeganą wiarygodność jeśli model źle
  oceni co jest „pokryte".
