# Przegląd: „Analiza dokumentów" (`/analiza`) — stan i kierunki ulepszeń — 2026-09-02

Cel: odpowiedź na pytanie „jak możemy ulepszyć analizę dokumentów". Przegląd kodu
(`AnalysisRunner`, `AnalysisPrompts`, `LegalUnitSplitter`, `AnalysisFollowUp`, `Analiza.razor`),
planów (DOC, SPK/AN, KAZ) i dwóch wcześniejszych raportów (jakość 07-23, niezawodność 09-02).
To dokument decyzyjny, nie zlecenie implementacji. Oznaczenia: **[FAKT]** z kodu/danych,
**[OCENA]** moja interpretacja, **[DO POMIARU]** hipoteza wymagająca liczby.

## 1. Gdzie jesteśmy

**[FAKT]** Funkcja działa za flagą `Analysis:Enabled`. Przepływ: PDF (tylko born-digital) →
podział na jednostki § / art. / pkt / akapity → dla KAŻDEJ jednostki pełny pipeline czatu RAG
(pytanie użytkownika + treść jednostki → retrieval korpusu → odpowiedź z werdyktem w pierwszej
linii) → raport składany mechanicznie + streszczenie LLM → dopytania.

**[FAKT]** Oś niezawodności została zamknięta dzisiaj (commit `7af7568`): osobna pula limitów per
dokument, auto-retry na błędy przejściowe i pustą odpowiedź, logowany zapis, `InterruptReason`,
bramka intencji. Z tamtego raportu otwarte pozostaje tylko: pomiar czasu (~4 min/fragment,
30+ min na umowę) i logowanie `finish_reason`.

**[FAKT]** Oś jakości nie ruszyła się od raportu 07-23. Wszystkie cztery pytania otwarte z tamtego
raportu są nadal otwarte: retrieval po sygnaturze (brak kodu w `PrawoRAG.Retrieval`), rozróżnienie
kategorii „BRAK ŹRÓDEŁ", krok „stan faktyczny" przed fazą map, streszczenie nieodpowiadające na
pytanie użytkownika.

**[FAKT]** Skala użycia: 20 analiz, 4 testerów, 148 jednostek w 6 tygodni. 25% jednostek to
BRAK ŹRÓDEŁ. Nie ma golden setu dla analizy; feedback per jednostka (AN-6) istnieje, ale N jest
zbyt małe, by cokolwiek z niego wnioskować. Jedyny materiał z kluczem odpowiedzi to
`TEST-ZALACZNIK-UMOWA-NAJMU.md` (umowa z wbudowanymi naruszeniami).

## 2. Diagnoza: co ogranicza wartość dla prawnika

**[OCENA]** Analiza to dziś „czat per paragraf". Sufit jakości = jakość czatu, ale sama
konstrukcja gubi to, co stanowi wartość przeglądu umowy przez prawnika. Trzy luki strukturalne:

**A. Fragment oceniany w izolacji.** `AnalysisPrompts.MapQuestion` buduje prompt wyłącznie z
treści jednej jednostki. Model nie wie, czy to umowa najmu lokalu mieszkalnego czy B2B, kto jest
konsumentem, co zdefiniowano w § 1. Ogólna klauzula („Wynajmujący może podwyższyć czynsz")
dostaje ogólną ocenę zamiast oceny w kontekście tej umowy. To był przypadek fragmentu 3 z
raportu 07-23; użytkownik sam zaproponował wtedy krok „stan faktyczny".

**B. Retrieval napędzany brzmieniem klauzuli, nie zagadnieniem prawnym.** Zapytanie do korpusu to
prompt użytkownika + tekst jednostki. Klauzula o kaucji „25-krotność czynszu" nie zawiera słów
„ochrona praw lokatorów" ani „art. 6". Retrieval musi trafić w normę bezwzględnie obowiązującą
po podobieństwie do tekstu klauzuli, co jest słabą kotwicą. Model ocenia tylko względem tego, co
retrieval przyniósł, więc jeśli norma nie weszła do top-K, werdykt OK jest fałszywie uspokajający.
To najgroźniejszy tryb błędu: nie odmowa, lecz przepuszczenie ryzyka.

**C. Werdykt płaski i nieakcjonowalny.** OK / RYZYKO / BRAK ŹRÓDEŁ. RYZYKO bez wagi i bez „co
zmienić". BRAK ŹRÓDEŁ zlepia trzy różne sytuacje: (1) fragment bez żadnej treści prawnej
(komparycja, dane stron), (2) fragment odwołuje się do aktu poza zakresem korpusu (plan
miejscowy, regulamin zewnętrzny), (3) prawdziwa luka korpusu. Tylko (3) jest defektem, a UI
pokazuje wszystkie trzy identycznie, więc ćwierć raportu wygląda na awarię. Streszczenie z zakazem
nowych twierdzeń nie odpowiada wprost na pytanie użytkownika („czy warto się odwołać?").

**[OCENA]** Do tego dochodzą trzy bariery użycia, niezależne od jakości: tylko PDF (umowy
powstają w Wordzie), 30+ minut na dokument (nieinteraktywne), brak eksportu raportu (raport
istnieje tylko w aplikacji, a gwiazda północna to memo, które prawnik zabiera dalej).

## 3. Kandydaci na ulepszenia (uporządkowane)

Kolejność wynika z zasady: najpierw metryka wyniku i warunek zabicia, potem tanie zmiany
strukturalne, potem drogie. Bez wycen czasowych.

### K1. Golden set analizy (warunek wstępny dla wszystkiego poniżej)

4–6 dokumentów z kluczem odpowiedzi: umowa najmu lokalu mieszkalnego (już jest, z wbudowanymi
naruszeniami), umowa z konsumentem (klauzule abuzywne), regulamin sklepu internetowego, decyzja
administracyjna z powołanym orzecznictwem, pismo procesowe. Dla każdego § oczekiwany werdykt
i oczekiwana norma. Metryki:

| metryka | co mierzy |
|---|---|
| recall wbudowanych ryzyk | ile z zaplanowanych naruszeń dostało RYZYKO |
| fałszywe RYZYKO | ile poprawnych § oznaczono jako ryzyko |
| BRAK ŹRÓDEŁ na § z treścią prawną | prawdziwe luki vs poprawne odmowy |
| czas ściany per dokument | z `PRAWORAG_LOG_TIMING` |

Złożoność: mała (harness `PrawoRAG.Eval` istnieje, tryb `--chat` też). Ryzyko: żadne. Bez tego
każda zmiana promptu poniżej jest „na oko".

### K2. Profil dokumentu przed fazą map (luka A)

Jedno dodatkowe wywołanie LLM per dokument: typ dokumentu, strony i ich role (konsument /
przedsiębiorca), przedmiot, definicje z części ogólnej, powołane akty i orzeczenia. Wynik
doklejany do KAŻDEGO promptu fazy map i do zapytania retrievalu jako kotwica dziedzinowa
(„umowa najmu lokalu mieszkalnego, najemca osoba fizyczna" ciągnie ustawę o ochronie praw
lokatorów nawet dla klauzuli, która jej nie wymienia).

Zabezpieczenie (uwaga z raportu 07-23 o zarażaniu ocen między fragmentami): profil to
WYŁĄCZNIE fakty z dokumentu, zero ocen prawnych; twardo w prompcie i weryfikowalne w evalu
(profil nie zawiera słów „narusza", „niezgodne", cytowań [n]).

Złożoność: mała. Koszt ruchu: +1 wywołanie na 10–40. Oczekiwany efekt **[DO POMIARU]**:
wzrost recallu na klauzulach ogólnych i spadek fałszywych OK.

### K3. Rozpoznanie zagadnienia przed retrievalem + pomijanie jednostek bez treści prawnej (luka B, czas)

Dla każdej jednostki krótki krok „jakie pytania prawne rodzi ta klauzula?" (model Aux, ten sam,
którego używa bramka intencji), wynik 0–2 pytania w formacie liniowym (wzorzec z planu KAZ:
`ZAGADNIENIE: ...`, parsowany twardo). Potem:

- 0 zagadnień → werdykt „BEZ TREŚCI PRAWNEJ" bez retrievalu i bez pełnego wywołania LLM.
  Dziś to ~25% jednostek, każda kosztuje ~4 minuty. To prawdopodobnie największa tania
  dźwignia czasu **[DO POMIARU]**, zgodna z warunkiem „bez utraty jakości" (te jednostki i tak
  dostają odmowę).
- 1–2 zagadnienia → retrieval per zagadnienie (istniejąca infrastruktura QU/routera), źródła
  łączone, dopiero potem prompt fazy map z treścią jednostki i źródłami.

Złożoność: średnia (nowy krok w `AnalysisRunner`, nowe prompty, testy). Ryzyko: Aux przepuści
klauzulę z ukrytym ryzykiem jako „bez treści prawnej". Mitygacja: asymetria promptu w stronę
„jest zagadnienie" (fałszywe pominięcie droższe niż zbędny retrieval), mierzona w K1.

### K4. Bogatszy werdykt i raport, który odpowiada na pytanie (luka C)

- Werdykty: OK / RYZYKO WYSOKIE / RYZYKO NISKIE / BEZ TREŚCI PRAWNEJ / POZA ZAKRESEM KORPUSU
  (akt prawa miejscowego, dokument zewnętrzny) / BRAK PODSTAWY W ŹRÓDŁACH. Dla RYZYKO
  obowiązkowa linia „Narusza: ..." i „Do rozważenia: ..." (co zmienić w klauzuli).
- Nagłówek raportu liczony mechanicznie, bez LLM: „3 z 14 § z ryzykiem: § 5, § 7, § 12;
  2 § poza zakresem korpusu". To rozwiązuje pytanie 4 z raportu 07-23 bez łamania zasady
  „zero nowych twierdzeń prawnych": streszczenie LLM dostaje ten nagłówek i ma prawo napisać
  meta-wniosek wyłącznie z werdyktów.
- UI: sekcje „bez treści prawnej" domyślnie zwinięte, „poza zakresem" z jednym zdaniem
  wyjaśnienia zamiast generycznej odmowy.

Złożoność: mała (prompt, parser, UI). Można zrobić razem z K2 w jednym kroku.

### K5. Wejście DOCX

Umowy powstają w Wordzie; wymuszanie „zapisz jako PDF" to tarcie przy każdym użyciu. Plan DOC
oznaczył to jako „technicznie łatwe, po feedbacku". Ekstrakcja tekstu z DOCX (OpenXML) daje
lepszą strukturę niż PdfPig (zachowane akapity, nagłówki), co poprawi też podział na jednostki.
OCR skanów nadal poza zakresem (osobna decyzja koszt/model).

Złożoność: mała.

### K6. Eksport raportu

Kopiuj / drukuj / PDF albo DOCX z raportu (werdykty, uzasadnienia, źródła z linkami). Dziś
raport żyje tylko w aplikacji. To pierwszy krok do „memo" z gwiazdy północnej i realny powód,
by prawnik wrócił. Złożoność: mała do średniej (rendering po stronie serwera).

### K7. Wydajność: najpierw pomiar, potem dźwignie

Zgodnie z raportem niezawodności: jeden przebieg z `PRAWORAG_LOG_TIMING` na dokumencie ~15
jednostek, logowanie `finish_reason`. Dopiero potem: cache wspólnego prefiksu promptu
(profil z K2 dodatkowo wydłuża wspólny prefiks, więc K2 i cache się wzmacniają), równoległość
na backendzie z batchingiem. K3 (pomijanie jednostek bez treści prawnej) daje zysk niezależnie
od wyniku pomiaru.

### K8. Weryfikacja powołanych orzeczeń (użycie: decyzje, pisma)

Most orzeczenie→orzeczenie: cytat z sygnaturą → dokładne dopasowanie po `CaseNumber`; cytat
opisowy („WSA w Gdańsku z 10.03.2011") → sąd + data. Nadal niezaimplementowane. Istotne tylko
dla przypadku „sprawdź, czy organ prawidłowo powołuje orzecznictwo", nie dla przeglądu umów.
Decyzja po K1: jeśli golden set i realne użycie pokażą, że dokumenty typu decyzja/pismo to
istotna część ruchu, wchodzi; jeśli dominują umowy, czeka.

Złożoność: średnia (nowa ścieżka retrievalu po metadanych, parser cytatów).

### K9. Drobne

- `LegalUnitSplitter` tnie § dłuższe niż 3500 znaków na „(cz. n)" po spacji, w środku ustępu;
  najpierw próbować cięcia na granicy „ust." / „pkt".
- Prompt fazy map żąda „1–3 zdań", model pisze rozbudowane analizy i to jest dobre; dopasować
  instrukcję do pożądanego kształtu (K4) zamiast udawać, że ma być krótko.
- Dopytania w trybie z archiwum nie mają treści dokumentu (świadoma decyzja prywatności);
  komunikat jest, zostawić.

## 4. Czego NIE robić

- **Nie powiększać korpusu jako naprawy jakości analizy.** Raport 07-23 pokazał, że więcej
  podobnych orzeczeń to więcej konkurentów o te same sloty top-K, nie więcej trafień.
- **Nie wracać do jednego promptu na cały dokument.** Spike SPK dowiódł, że daje jedną zbiorczą
  odpowiedź zamiast analizy punkt po punkcie.
- **Nie podnosić `MaxParallelism` na lokalnej karcie.** Generacja jest sekwencyjna; zysk zero.
- **Nie zmieniać promptów przed K1.** Bez klucza odpowiedzi każda zmiana jest nieweryfikowalna,
  a prompty są strojone pod konkretny model.

## 5. Rekomendowana kolejność

1. **K1** golden set (warunek zabicia dla reszty).
2. **K2 + K4** w jednym kroku: profil dokumentu, bogatszy werdykt, mechaniczny nagłówek
   raportu. Pomiar na K1 przed i po.
3. **K7** pomiar czasu (zero kodu) równolegle z 2.
4. **K3** rozpoznanie zagadnienia + pomijanie jednostek bez treści prawnej. Pomiar recallu
   i czasu.
5. **K5** DOCX i **K6** eksport, bo znoszą tarcie użycia niezależnie od jakości.
6. **K8** weryfikacja orzeczeń, jeśli ruch to uzasadni.

Warunek zabicia dla całej ścieżki jakości: jeśli po K2+K4 recall wbudowanych ryzyk na golden
secie nie rośnie, wąskim gardłem jest model (jak przy OKI po rozszerzeniu sąsiedztwa), a nie
struktura pipeline'u; wtedy K3 nie ma sensu bez zmiany backendu LLM.

## 6. Czego nie sprawdzono

- Rozkład promptów użytkowników i werdyktów z bazy produkcyjnej (zapytanie do
  `192.168.100.11` zablokowane przez klasyfikator uprawnień w tej sesji); liczby z sekcji 1
  pochodzą z raportu niezawodności 09-02.
- Realny rozkład czasu per etap (K7 to pomiar, nie wiedza).
