# Diagnoza: przestarzała treść starej nowelizacji wygrywa z aktualnym przepisem — nowy mechanizm w serii

Data: 2026-09-01. Punkt wyjścia: pytanie "Jakie wynagrodzenie przysługuje mi za nadgodziny?" dostało
odpowiedź cytującą jako źródło [1] **uchyloną od 2004 roku** stawkę "50% za dwie pierwsze godziny
nadliczbowe na dobę" (nowelizacja z 1996 r.), podczas gdy aktualny, obowiązujący przepis (art. 151¹ §1
k.p., stawki 100%/50% zależne od pory/dnia pracy, wprowadzony nowelą z 2003 r.) w ogóle nie trafił do
puli źródeł.

**To NOWY mechanizm w tej serii diagnoz** — inny niż niedopasowanie słownictwa
(`DIAGNOZA-TERMIN-ZASWIADCZENIE-OSWIADCZENIE`, `DIAGNOZA-ZNAK-WODNY-AI-ACT`) i inny niż rozmycie
długiego artykułu (`DIAGNOZA-NAJEM-LOKATOR-CHUNK-ROZMYCIE`). Tu problemem nie jest ani dobór słów, ani
długość chunka — to **przestarzała treść merytoryczna nowelizacji, która brzmi lepiej (krótsza,
czystsza) niż aktualny przepis, i wygrywa z nim wprost w embeddingu**.

## [FAKT — zmierzone sondą] Przestarzały przepis wygrywa o dwa rzędy wielkości

| Kandydat | Treść | exact fp32 | HNSW (ef=400) | Pula RRF |
|---|---|---|---|---|
| **DU/1996/110, art. 1 §1 pkt 1** (wariant 4/12) — **UCHYLONY w 2004 r.** | "50% wynagrodzenia — za pracę w dwóch pierwszych godzinach nadliczbowych na dobę" | **#39** (sim=0,8161) | **#38** | **#38/50 — w puli** |
| **DU/1974/141, art. 151¹ §1** (Kodeks pracy, **aktualnie obowiązujący**) | "100% — w nocy/niedziele/święta; 50% — w pozostałe dni" | **#443** (sim=0,7930) | **nieobecny** w top-200 | **poza pulą** |

Różnica jest nie o włos, tylko o **ponad 400 pozycji** w dokładnym rankingu. Przestarzały przepis
wygrywa zdecydowanie, nie na styk.

## Mechanizm — krótki, wyizolowany "wariant" nowelizacji bije dłuższy, kompletny przepis

`DU/1996/110` to ustawa nowelizująca z 1996 r., która hurtowo zmieniała dziesiątki artykułów Kodeksu
pracy naraz. Chunker/normalizer dzieli taki wielki blok zmian na osobne **"warianty"** — jeden chunk
na jedną zmienioną jednostkę. Chunk będący źródłem [1] to `Art. 1 § 1 pkt 1 (wariant 4/12)`:

```
1)
50% wynagrodzenia - za pracę w dwóch pierwszych godzinach nadliczbowych na dobę,
```

Zaledwie **43 tokeny**, jedno zdanie, tematycznie idealnie czyste — embedding "widzi" wyłącznie
"wynagrodzenie za nadgodziny", bez żadnego rozcieńczenia. Aktualny art. 151¹ §1 Kodeksu pracy ma
**117 tokenów** i mieści obie stawki razem z rozbudowanymi warunkami (pora nocna, niedziele, święta,
dzień wolny w zamian) — więcej treści w jednym wektorze, więc mniej "ostry" sygnał dla prostego
pytania.

To jest **odwrotność mechanizmu z diagnozy o lokatorach** (tam krótki chunk wygrywał, bo był
tematycznie WŁAŚCIWY, tylko z innego przepisu; tu krótki chunk wygrywa mimo bycia merytorycznie
**nieaktualnym**). Ten sam efekt uboczny "krótszy = czystszy embedding", ale tym razem uderza w
poprawność przez czas, nie przez temat.

## Drugi, niezależny czynnik: trzy różne wersje tego samego przepisu w puli, żadna nie jest tą właściwą z kodeksu

Pełna lista źródeł z tej odpowiedzi pokazuje coś dodatkowo niepokojącego — w puli 11 źródeł, TRZY
oddzielne akty niosą kolejne historyczne wersje tego samego przepisu:

1. `DU/1996/110` art. 1 §1 pkt 1 — stawka z 1996 r. (UCHYLONA), źródła [1] i [4] (zduplikowane)
2. `DU/2002/1146` art. 1 §1 — nowelizacja z 2002 r. (też już nieaktualna), źródło [2]
3. `DU/2003/2081` art. 151_1 §1 — **treść nowelizacji z 2003 r., która wprowadziła DZISIEJSZE brzmienie** — źródło [5]

Źródło [5] ma merytorycznie **poprawną, aktualną treść** (bo to właśnie ta nowela ustanowiła
dzisiejszy przepis) — ale model zacytował w pierwszym zdaniu odpowiedzi (konkretne stawki procentowe)
źródło [1], czyli wersję z 1996 r., a nie [5]. To znaczy, że oprócz problemu retrievalu (aktualny
tekst skonsolidowanego kodeksu, art. 151¹ §1 z `DU/1974/141`, w ogóle nie wszedł do puli) jest też
**problem syntezy LLM**: mając w puli źródeł zarówno starą, jak i nową wersję tego samego przepisu,
model wybrał starszą do zacytowania pierwszej, kluczowej informacji (wysokość dodatku), mimo że
poprawna wersja (źródło [5]) była dostępna równolegle.

## Dlaczego to prawdopodobnie nie jest odosobniony przypadek

Kodeks pracy, podobnie jak KPK czy KPC, ma za sobą dziesiątki nowelizacji na przestrzeni 50 lat.
Każda taka nowelizująca ustawa zostaje w korpusie jako pełnoprawny, `InForce=true` dokument, i każdy
jej "wariant" (per-zmieniana jednostka) to osobny, krótki, czysto zaembedowany chunk — dokładnie ten
kształt, który wygrywa z długim, kompletnym przepisem w konsolidowanym tekście kodeksu. Każdy przepis,
który był wielokrotnie nowelizowany i ma krótkie, jednozdaniowe "warianty" zmian w swojej historii,
jest kandydatem na ten sam błąd — nie tylko przepisy o nadgodzinach.

## Otwarte — nieoceniona jeszcze skala i kierunek naprawy

- **Skala nieznana** (n=1 zmierzony przypadek). Warto sprawdzić inne wielokrotnie nowelizowane
  przepisy Kodeksu pracy/KPC/KPK pod kątem tego samego wzorca, zanim ktokolwiek zdecyduje o priorytecie.
- **To NIE jest problem "szumu przypisów"** już naprawiony w `517d5ef` — tamten fix usuwał chunki
  zdominowane przez SAM WYKAZ historii nowelizacji (bibliografia), nie substancyjną treść starych
  wariantów zmian. To odrębny, nieobjęty dotąd problem.
- **Kierunek naprawy nieoceniony**, kilka możliwych, żaden niewdrożony:
  (a) obniżyć wagę/wykluczyć z retrievalu chunki nowelizacji, których treść została już **wchłonięta**
      do aktualnego tekstu jednolitego innego dokumentu (system ma już pojęcie "unabsorbedAmendments" —
      `IX_documents_UnabsorbedAmendments` — może dałoby się to odwrócić: jeśli akt bazowy JEST wchłonięty,
      jego chunki-warianty nie powinny konkurować z konsolidowanym tekstem w retrievalu na żywo);
  (b) w promptcie/syntezie: gdy pula zawiera kilka źródeł tego samego artykułu z różnych lat, jawnie
      preferować najnowszą datę aktu przy cytowaniu liczb/stawek;
  (c) chunking: nie tworzyć aż tak krótkich, "czystych" chunków z historycznych nowelizacji, żeby nie
      wygrywały nieproporcjonalnie z dłuższym, kompletnym tekstem aktualnym.

## Narzędzie

`--probe-chunk` (dwukrotnie, oba konkurenci) + bezpośrednie zapytania SQL do `messages.RetrievedSources`
na produkcyjnej bazie, żeby zobaczyć pełną listę 11 źródeł i porównać które wersje przepisu faktycznie
trafiły do puli.
