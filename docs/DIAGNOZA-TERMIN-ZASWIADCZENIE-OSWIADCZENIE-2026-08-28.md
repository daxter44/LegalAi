# Diagnoza: dosłowne słowo użytkownika przeważa nad znaczeniem — „zaświadczenie" zamiast „oświadczenie"

Data: 2026-08-28. Punkt wyjścia: pytanie użytkownika o formę elektronicznego złożenia dokumentu, żeby
zostać oskarżycielem posiłkowym. System poprawnie znalazł podstawę materialną, ale dla pytania
o formę elektroniczną odmówił — mimo że właściwy przepis jest w korpusie, na temat, łatwy do
znalezienia.

## Obserwacja (eksperyment A/B na żywym systemie, nie sonda)

**Pytanie A** (dosłowne sformułowanie użytkownika, słowo „zaświadczenie"):

> „Dostałem pismo z informacją, że mogę zostać oskarżycielem posiłkowym w sprawie i muszę złożyć
> **zaświadczenie** do właściwego sądu. Czy można to zrobić przez epuap/e-doręczenia czy jakkolwiek
> internetowo?"

Odpowiedź: **odmowa** — "Źródła nie pozwalają odpowiedzieć... dokumentacja nie określa trybu
elektronicznego dla tej konkretnej czynności." Cytowane źródła dla części elektronicznej ([1], [19],
[14], [15], [5][6][10][11][13]) — model sam scharakteryzował je jako dotyczące **wyłącznie osób
ubiegających się o powołanie na stanowisko sędziowskie** — zupełnie inny temat.

**Pytanie B** (to samo pytanie, jedno słowo zamienione: „zaświadczenie" → „oświadczenie"):

> „...muszę złożyć **oświadczenie** do właściwego sądu..."

Odpowiedź: **pełna, poprawna, na temat** — wymienia trzy realne drogi elektroniczne (portal
informacyjny z potwierdzeniem wniesienia pisma, formularz w systemie teleinformatycznym sądu,
„pismo ogólne" na ePUAP przez elektroniczną skrzynkę podawczą) i poprawnie opisuje wymogi
identyfikacji (podpis zaufany ePUAP / kwalifikowany certyfikat / identyfikator z sądu).

## [FAKT — zmierzone w bazie] Obie hipotezy zweryfikowane bezpośrednio

1. **Właściwe przepisy istnieją i są na temat.** `DU/1997/555` (k.p.k.):
   - Art. 54 §1 — materialna podstawa (oświadczenie pokrzywdzonego o działaniu jako oskarżyciel
     posiłkowy) — **trafiony poprawnie w obu pytaniach** (źródło [20]/[1] w odpowiednich odpowiedziach).
   - Art. 116 §2-3, 116a, 119 — forma elektroniczna oświadczeń/pism procesowych: kwalifikowany
     podpis elektroniczny, podpis zaufany, adres do doręczeń elektronicznych, portal informacyjny.
     Dokładnie to, o co pyta użytkownik — ale w Pytaniu A **nie zostały zacytowane w ogóle**.
   - Art. 54 i art. 116 **nie dzielą słownictwa** — art. 54 nie wspomina o formie elektronicznej,
     art. 116 nie wspomina o oskarżycielu posiłkowym (to ogólny przepis proceduralny, wspólny dla
     wszystkich oświadczeń w k.p.k.). To DWA rozłączne przepisy tego samego aktu, które trzeba
     świadomie połączyć — bez jawnego cytowania jednego przez drugi (inny mechanizm niż w
     `DIAGNOZA-NOWELIZACJA-DATA-WEJSCIA-W-ZYCIE`, gdzie klauzula wprost wskazywała numer przepisu).
   - **Podnóż z jednym słowem popsuł tylko połowę odpowiedzi** — nie odgadnięcie podstawy (art. 54
     został znaleziony w OBU wariantach), tylko odgadnięcie, JAK to zrobić elektronicznie.

2. **Fałszywe trafienie ma realne, zidentyfikowane źródło.** Zapytanie `websearch_to_tsquery`
   (`'zaświadczenie stanowisko sędziowskie'`) na całym korpusie zwraca realne orzeczenia dotyczące
   sporów o powołania sędziowskie (sygnatury „III KRS 7/07", „III KRS 8/07", „III PO 1/10" i inne) —
   dokładnie ta sama tematyka, którą model sam wskazał jako źle dobrane źródło. To nie halucynacja
   nieistniejącego dokumentu — to **realne, ale niewłaściwe** dopasowanie: "zaświadczenie" jako
   dosłowny token istnieje obficie w zupełnie innej gałęzi prawa (procedury awansu sędziowskiego,
   gdzie kandydaci faktycznie składają liczne zaświadczenia), i to ten klaster wygrał podobieństwem
   z powodu dosłownego użycia słowa przez użytkownika.

## Mechanizm

Użytkownik napisał **kolokwialnie** "zaświadczenie", mając na myśli deklarację/oświadczenie, o którym
mówi art. 54 k.p.k. — to zrozumiała pomyłka terminologiczna (oba słowa w języku potocznym bywają
używane zamiennie, choć prawnie oznaczają coś innego: zaświadczenie wydaje organ, oświadczenie
składa strona). Dense retrieval jest **wierny dosłownemu słowu w zapytaniu**, nie znaczeniu
zamierzonemu przez użytkownika — więc zamiast uogólnić "zaświadczenie" → "oświadczenie" (te słowa
NIE są sobie bliskie semantycznie w przestrzeni embeddingu, mimo pozornego podobieństwa dla
człowieka), znalazł najbliższy dosłowny sens: prawdziwe "zaświadczenia" składane w innej,
niepowiązanej procedurze.

Drugi, niezależny czynnik: nawet gdyby model poprawnie rozpoznał zamierzone słowo, art. 54 i art. 116
nie dzielą słownictwa — to wymaga połączenia dwóch generycznie sformułowanych przepisów tego samego
aktu, bez żadnego jawnego łącznika między nimi (w przeciwieństwie do mostu vacatio legis, gdzie
klauzula WPROST wskazuje numer przepisu do dociągnięcia).

## Dlaczego to prawdopodobnie nie jest odosobniony przypadek

Użytkownicy nieprawniczy regularnie mylą pary bliskoznacznych, ale prawnie odrębnych terminów:
zaświadczenie/oświadczenie, wniosek/pozew, odwołanie/zażalenie/skarga, umowa/porozumienie. Każda
taka para to potencjalny punkt, w którym dosłowne dopasowanie leksykalne przeważy nad zamierzonym
znaczeniem — dokładnie analogiczny mechanizm do już zdiagnozowanego wcześniej przypadku OKI (błędny,
ale wiarygodnie wyglądający wynik tego samego rzędu co poprawny), tylko wyzwalany przez słowo
użytkownika, nie przez strukturę dokumentu.

## Otwarte — nieoceniona jeszcze skala i kierunek naprawy

- **Skala nieznana** (n=1 zmierzony przypadek, zweryfikowany A/B, nie stopa błędu). Warto sprawdzić
  pozostałe pary bliskoznaczne wymienione wyżej, zanim ktokolwiek zdecyduje, czy to wart osobnego
  mechanizmu problem, czy margines.
- **Kierunek naprawy nieoceniony**: (a) słownik synonimów/normalizacja zapytania przed embeddingiem
  (ryzyko: więcej fałszywych trafień gdzie indziej), (b) query rewriting przez LLM przed retrievalem
  (koszt dodatkowego wywołania — ten sam kompromis co `GapClosingRetrieval`), (c) nic nie robić i
  polegać na tym, że `GapClosingRetrieval` (druga runda przy słabym pokryciu) czasem to złapie —
  ale w tym przypadku NIE złapało (odpowiedź była odmową, nie błędnym audytem, prawdopodobnie
  dlatego że sygnał similarity na fałszywych źródłach był wystarczająco wysoki, żeby nie wyzwolić
  drugiej rundy — do zweryfikowania osobno, nie zakładam).

## Narzędzie

Nie sonda (`--probe-chunk` zawiesił się przy tej analizie, przyczyna nieustalona) — czysty eksperyment
A/B na żywym systemie (dwa pytania różniące się jednym słowem) plus bezpośrednie zapytania
`websearch_to_tsquery` do bazy, weryfikujące istnienie i tematykę fałszywie trafionych źródeł.
