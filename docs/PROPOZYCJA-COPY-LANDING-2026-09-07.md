# Propozycja copy landingu — inspirowana stroną Omnilexii (2026-09-07)

Źródło landingu: `src/PrawoRAG.Api/Program.cs`, stała `LandingHtml` (linie ~297–517).
Materiał porównawczy: strona główna Omnilexii (tekst przekazany przez właściciela 2026-09-07).
Dokument proponuje **gotowe teksty do wklejenia** — decyzja i wdrożenie po stronie właściciela.

---

## 0. Blokada przed czymkolwiek innym: placeholdery w produkcyjnym HTML

W `LandingHtml` siedzą nawiasy kwadratowe, które wyrenderują się użytkownikowi dosłownie:

Lista aktualna (zweryfikowana 2026-09-07) — **sprawdzaj poleceniem, nie numerem linii**, bo
numery przestaną się zgadzać po pierwszej edycji:

```bash
grep -n '\[[^]]\{4,\}\]' src/PrawoRAG.Api/Program.cs | sed -n '/LandingHtml\|span\|amount/p'
```

| Linia | Tekst |
|---|---|
| 427 | `[PYTANIE O PRZEPIS OBJĘTY ŚWIEŻĄ NOWELIZACJĄ]` |
| 431 | `[ODPOWIEDŹ OGÓLNEGO CZATU — pewna siebie, oparta na brzmieniu…]` |
| 434 | `NOWELIZACJA — WEJDZIE W ŻYCIE [DATA]` |
| 435 | `[ODPOWIEDŹ OMNIASI — zestawia dotychczasowy i nowy stan prawny…]` |
| 438 | `odpowiedzi z [DATA POROWNANIA]` |
| 501 | `[CENA] zł / miesiąc` |

To jest większy problem niż którykolwiek nagłówek. Cała karta „Nowelizacje pod kontrolą" —
najmocniejszy blok na stronie, bo pokazuje **nasz jedyny niekopiowalny wyróżnik** — jest dziś
pustym szkieletem. **Do wypełnienia realnym przypadkiem, nie wymyślonym**: mamy gotowy w
`project_missing_unabsorbed_amendment_link` (DU/2018/646 → DU/2025/1168) i w
`DIAGNOZA-NOWELIZACJA-DATA-WEJSCIA-W-ZYCIE-2026-08-27.md`. Zrzut z Gemini/ChatGPT na to samo
pytanie zajmuje 5 minut i daje materiał, którego konkurencja nie ma.

---

## 1. Diagnoza: co ich strona robi, a nasza nie

Cztery mechanizmy, nie cztery teksty:

1. **Liczby jako kręgosłup.** „13 baz", „3 936 765 dokumentów", „0 halucynacji", „15 minut".
   Polski kupujący profesjonalny czyta liczby, nie metafory. **Nasza strona nie ma ani jednej
   liczby** poza limitami planów — a mamy zmierzone rzeczy, których nie pokazujemy.
2. **Nagłówek z podmiotem i przedmiotem.** „Jedna platforma dla kancelarii i działów prawnych:
   research prawny w 13 bazach…" — od razu wiadomo *dla kogo* i *co dostajesz*. Zero poezji.
   Twoja uwaga o „Zna źródła" jest trafna i to jest dokładnie ta różnica.
3. **„Dla kogo" rozpisane na segmenty** — małe kancelarie, duże kancelarie, in-house, doradcy
   podatkowi, każdy z własną korzyścią. U nas: brak. Czytelnik musi sam zgadnąć, czy to dla niego.
4. **FAQ zbijające obiekcje zakupowe** — promptowanie, ChatGPT, bezpieczeństwo, cena, test.
   U nas: brak. Człowiek z pytaniem „a ile to kosztuje i czy mogę spróbować" nie ma gdzie pójść.

Czego **nie** przejmujemy — §9.

---

## 2. Hero — trzy warianty, z rekomendacją

**Obecnie:**
> H1: „Zna źródła<br>każdej swojej odpowiedzi."
> Lead: „Przepisy z pilnowaniem nowelizacji, orzecznictwo, cytowania do zweryfikowania jednym
> kliknięciem — a gdy źródła nie wystarczają, OmniaSI mówi to wprost, zamiast zgadywać."

Diagnoza: H1 bez podmiotu (kto zna?), „zna źródła" brzmi jak „orientuje się w literaturze",
a nie „podaje sygnaturę do sprawdzenia". Lead to jedno zdanie z trzema ideami i myślnikiem
w środku — czyta się raz, nie zapada.

### Wariant A (rekomendowany) — uderza w największy strach prawnika przed AI

> **H1:** OmniaSI nie zmyśla sygnatur.
> **Lead:** Każda teza z cytatem, który otwierasz jednym kliknięciem. A kiedy w źródłach nie ma
> podstawy — powie to wprost, zamiast zgadywać.

Dlaczego to: ma podmiot (marka), jest krótkie, i trafia w jedyną rzecz, którą **każdy** prawnik
o AI już słyszał — zmyślone sygnatury wyroków. Ani lexedit, ani Omnilexia tego nie mówią wprost,
bo obaj sprzedają „są prawdziwe źródła" (obietnica pozytywna), a nie „nie zmyślamy" (obietnica
negatywna, mocniejsza, bo ryzykowna dla mówiącego).

### Wariant B — bliżej ich schematu: dla kogo + co dostajesz

> **H1:** Research prawny dla kancelarii i działów prawnych — z sygnaturą przy każdym zdaniu.
> **Lead:** Przepisy, orzecznictwo i prawo Unii w jednym pytaniu zadanym po polsku. Każdy cytat
> otwierasz w oryginale. Nowelizacje pilnowane — wiesz, która wersja przepisu obowiązuje dziś.

Dlaczego to: najbezpieczniejszy, najbardziej „korporacyjny", najlepiej znosi bycie wyciętym
do meta description i do reklamy.

### Wariant C — najostrzejszy, najbardziej ryzykowny

> **H1:** Sprawdzisz każdą naszą odpowiedź w pięć sekund.
> **Lead:** Bo pod każdą tezą jest cytat i link do oryginału. Nie prosimy Cię o zaufanie —
> dajemy Ci narzędzie do weryfikacji.

Dlaczego to: przenosi ciężar z „zaufaj AI" na „zweryfikuj AI", co jest dokładnie tym, jak myśli
prawnik. Ryzyko: „pięć sekund" to obietnica UX, którą trzeba dowieźć — panel źródeł musi być
naprawdę jednym kliknięciem na telefonie też.

**Eyebrow** (dziś: „Asystent researchu prawnego · dane i modele w UE") — zostawić, ale
**nie publikować „modele w UE" przed wdrożeniem na CloudFerro**; do tego czasu: „Asystent
researchu prawnego · dane w Unii Europejskiej".

---

## 3. Pasek liczb — nowa sekcja pod hero (największy pojedynczy zysk)

Ich pasek to cztery kafle. Nasz **nie może być kalką**, bo na liczbie dokumentów przegrywamy:
oni deklarują 3 936 765, my mamy ~540 tys. (≈17,3 tys. aktów PL + 2 916 aktów UE + ~520 tys.
orzeczeń). **Nie stawaj z nimi w tej konkurencji** — przegrasz nagłówkiem, który sam napisałeś.

Zamiast tego cztery osie, na których stoimy lepiej albo oni nie stoją wcale:

> **8,38 mln** — PRZESZUKIWANYCH FRAGMENTÓW
> Nie streszczenia — oryginalne fragmenty przepisów i uzasadnień.
>
> **2 916** — AKTÓW PRAWA UNII EUROPEJSKIEJ
> EUR-Lex od 2004 r. — obok polskich ustaw, w tym samym pytaniu.
>
> **2 488** — USTAW ZMIENIAJĄCYCH W KORPUSIE
> Nie tylko teksty jednolite. Ustawę zmieniającą widzisz powiązaną z aktem, który zmienia —
> także zanim jej treść wejdzie do tekstu jednolitego.
>
> **0** — ZMYŚLONYCH SYGNATUR, KTÓRE PRZEJDĄ DALEJ
> Każda odpowiedź przechodzi automatyczną walidację cytowań. Cytat bez pokrycia w źródle
> nie trafia do Ciebie.

⚠️ **Ostrzeżenie, bez którego ten pasek jest nieuczciwy.** Ostatni kafel celowo opisuje
**mechanizm**, a nie wynik pomiaru. Kuszące „0 halucynacji" jak u nich opieramy dziś na
**5 pozycjach** golden setu (`anty-halucynacja 100% (5/5)`, pomiar 2026-09-04) — n=5 nie unosi
hasła reklamowego i przy pierwszym kontrprzykładzie kosztuje wiarygodność całej strony. Bramka
cytatów istnieje w kodzie (`CitationGateEnabled`, `CitationValidator`) i jest opisywalna zawsze
prawdziwie. **Jeśli chcesz tam liczbę — najpierw rozszerz golden set do n≥30 na tej metryce.**

⚠️ **Druga pułapka, na której sam się złapałem.** Pierwotnie podpisałem ten kafel „ustaw
zmieniających POD KONTROLĄ". Liczba 2 488 (`ANALIZA-NADGODZINY-WCHLONIETE-NOWELE-POMIAR-2026-09-01.md`)
mierzy co innego: **akty „o zmianie…" obecne w korpusie** (2 488 z 17 378 aktów, 14%) — to jest
zasięg danych, nie dowód aktywnego pilnowania. Dowodem operacyjnym jest przebieg relinku
z 2026-09-01: **skanowanych 2 583, odświeżonych 130, błędów 0**. Jeśli chcesz kafel o *pilnowaniu*,
a nie o *posiadaniu* — użyj tamtych liczb i tak go opisz.

Liczby aktualne na 2026-09-02 (8,38 mln fragmentów) i 2026-09-01 (2 488 aktów zmieniających).
**Dwie z czterech pozycji tego paska wymagają przeliczenia zapytaniem do produkcyjnej bazy przed
publikacją** — pochodzą z dokumentów sesyjnych, nie z żywego korpusu.

---

## 4. Sekcja „Dla kogo" — nowa, wzór wprost od nich

Wstawić między blok analizy dokumentów a cennik.

> ## Dla kogo jest OmniaSI
> Research, który da się sprawdzić — niezależnie od tego, w jakiej roli sprawdzasz.
>
> **SZYBSZY PIERWSZY KROK W SPRAWIE**
> **Małe i średnie kancelarie**
> Pytasz po polsku, dostajesz przepisy i orzeczenia z sygnaturami do sprawdzenia. Zamiast godziny
> w wyszukiwarce — kilka minut i lista źródeł, od której zaczynasz pracę.
>
> **PRAWO POLSKIE I UNIJNE W JEDNYM PYTANIU**
> **Prawnicy in-house i compliance**
> RODO, AI Act, DSA, DORA — akty unijne obok polskiej ustawy wdrażającej, w jednej odpowiedzi.
> Bez przeskakiwania między EUR-Leksem a ISAP-em.
>
> **WIESZ, CO SIĘ ZMIENIŁO I OD KIEDY**
> **Praktycy w obszarach po nowelizacji**
> Prawo pracy, podatki, procedura — tam gdzie tekst jednolity nie nadąża. OmniaSI pokazuje
> nowelizację, która jeszcze nie weszła do tekstu, i datę jej wejścia w życie.
>
> **KAŻDA TEZA DO ZWERYFIKOWANIA**
> **Aplikanci i młodsi prawnicy**
> Nie musisz wierzyć odpowiedzi — otwierasz źródło i sprawdzasz. Plan Start (0 zł) wystarcza,
> żeby zobaczyć, czy to działa na Twoich sprawach.

Uwaga: **celowo nie ma segmentu „doradcy podatkowi"** — u nich to osobny kafel oparty na pełnej
bazie interpretacji MF, której nie mamy. Obiecywanie tego segmentu bez interpretacji podatkowych
kończy się rezygnacją po pierwszym pytaniu. Wraca, jeśli powstanie spike z
`ANALIZA-KONKURENCJA-OMNILEXIA-2026-09-07.md` §4.

---

## 5. FAQ — nowa sekcja przed stopką

Sześć pytań, każde zbija realną obiekcję zakupową:

> **Czym to się różni od ChatGPT albo Gemini?**
> Ogólny czat odpowiada z pamięci modelu i brzmi tak samo pewnie, gdy ma rację i gdy jej nie ma.
> OmniaSI najpierw znajduje przepis lub orzeczenie w korpusie, a potem odpowiada wyłącznie na
> podstawie znalezionych fragmentów — z cytatem, który otwierasz w oryginale. Gdy nie znajdzie
> podstawy, mówi o tym wprost.
>
> **Z jakich źródeł korzysta?**
> Kodeksy, ustawy i rozporządzenia z ISAP/ELI, prawo Unii Europejskiej z EUR-Lex (od 2004 r.)
> oraz orzecznictwo Sądu Najwyższego, sądów powszechnych i administracyjnych (SAOS). Wszystko
> z publicznych, państwowych baz — bez treści licencjonowanych od komercyjnych wydawców.

*(Nic mocniejszego w tym miejscu nie dopisywać: kwestia ToS SAOS i prawa sui generis do bazy jest
otwarta — `PLAN-KOMERCJALIZACJA.md` §7. Zdanie powyżej jest prawdziwe i wystarczy.)*
>
> **Co się dzieje z moimi pytaniami i dokumentami?**
> Nie trenują żadnego modelu. Wgrany dokument nie trafia do naszej bazy — żyje tylko w trakcie
> analizy; zapisujemy raport, nie treść umowy. Infrastruktura działa w Unii Europejskiej.

*(Obietnica zweryfikowana w kodzie 2026-09-07, także dla trwałej ścieżki `/analiza`:
`AnalysisStore.cs` — „Text (treści nie przechowujemy)"; `StoredUnit` niesie nagłówek, werdykt,
odpowiedź i źródła, a `ToSnapshot` odtwarza `DocUnit` z pustym tekstem. Zdanie można publikować.)*
>
> **Czy to zastępuje poradę prawną?**
> Nie. OmniaSI przygotowuje research do weryfikacji przez prawnika. Dlatego każda teza ma cytat —
> ostatnie słowo należy do Ciebie, nie do modelu.
>
> **Co robi, gdy nie zna odpowiedzi?**
> Mówi „nie znalazłem jednoznacznej podstawy prawnej" zamiast zgadywać. To jest funkcja, nie
> awaria: research, który czasem odmawia, jest użyteczniejszy niż taki, któremu nigdy nie wiadomo,
> kiedy wierzyć.
>
> **Czy mogę przetestować przed płaceniem?**
> Tak. Plan Start to 0 zł i 15 pytań miesięcznie, bez karty. Wystarczy, żeby zadać własne pytania
> z własnych spraw — a to jedyny test, który cokolwiek rozstrzyga.

---

## 6. Poprawki punktowe w istniejących blokach

| Miejsce | Dziś | Propozycja | Powód |
|---|---|---|---|
| Sekcja `#roznice`, H2 | „Ogólny chatbot odpowie na wszystko.<br>OmniaSI odpowiada za coś." | **zostawić** | Najlepsze zdanie na całej stronie. Lepsze niż ich „Dlaczego ogólna AI to za mało dla prawnika?" |
| Karta „Uczciwa odmowa", H3 | „Woli powiedzieć »nie wiem« niż zmyślić przepis." | „Powie »nie wiem«, zamiast zmyślić sygnaturę." | „Woli" antropomorfizuje i osłabia; „sygnatura" jest konkretniejsza niż „przepis" |
| Karta „Wszystko ze źródeł", H3 | „Każda teza z cytowaniem, każde cytowanie do sprawdzenia." | „Klikasz w cytat i lądujesz w oryginalnym dokumencie." | Czasownik i druga osoba zamiast rzeczownikowej symetrii; opisuje czynność użytkownika |
| Karta „Twoje sprawy…", H3 | „Pytania i dokumenty nie trenują żadnego modelu." | **zostawić**, dopisać zdanie: „Wgranej umowy nie zapisujemy — zostaje raport, nie treść." | Konkret, którego oni nie mają: ich Baza Organizacji *z definicji* przechowuje dokumenty |
| Blok analizy, H2 | „Wgraj umowę.<br>Dostaniesz analizę paragraf po paragrafie." | **zostawić** | Tryb rozkazujący + konkretny rezultat — działa |
| Tabela porównawcza, wiersz 4 | „…dane w UE" / „zależnie od planu" | dopisać wiersz: „Prawo Unii Europejskiej obok polskiego, w jednej odpowiedzi ✓ / ✕" | Jedyny wyróżnik, którego **żaden** z dwóch konkurentów nie reklamuje |
| Cennik, H2 | „Prosty cennik" | „Cennik bez gwiazdek" | „Prosty" to samochwalstwo; „bez gwiazdek" to obietnica sprawdzalna na tej samej stronie |
| Plan Pro, punkt 1 | „300 zapytań miesięcznie" | „300 pytań miesięcznie — ok. 15 dziennie w dni robocze" | Limit przetłumaczony na dzień pracy przestaje być ograniczeniem, a staje się miarą |
| Nawigacja | Czym się różnimy · Cennik · O systemie | + **Bezpieczeństwo** (kotwica do FAQ/§3) | U nich osobna pozycja w menu — dla kancelarii to pierwsze pytanie, nie ostatnie |

---

## 7. CTA końcowe — sekcja, której u nas w ogóle nie ma

Ich strona kończy się ofertą kontaktu („Bezpłatne demo – 30 minut"). Nasza kończy się stopką
prawną. Człowiek, który doczytał do końca, nie ma co kliknąć.

> ## Zadaj pierwsze pytanie z własnej sprawy
> Nie z demo, nie z przykładu — z tego, nad czym pracujesz dziś. To jedyny test, który
> cokolwiek rozstrzyga.
>
> [Zacznij za darmo — 15 pytań/mies.]   [Zobacz, jak działa research](/o-systemie)

Uwaga: **nie kopiujemy ich „bezpłatnego demo 30 minut"**. Demo z człowiekiem to model sprzedaży
zespołu handlowego, którego nie masz; obiecane i niedowiezione demo szkodzi bardziej niż jego
brak. Nasz odpowiednik to plan Start.

---

## 8. Czego świadomie NIE kopiujemy

- **„Asystenci AI bez promptowania" / Kreator / Biblioteka 30 asystentów** — nie mamy tego, a to
  jest oś ich całej strony. Obiecywanie tego to najkrótsza droga do rezygnacji w pierwszej minucie.
- **Liczba dokumentów jako kafel** — 3,9 mln vs ~540 tys.; §3.
- **„0 HALUCYNACJI" jako hasło** — n=5; §3.
- **„Zaufali nam" z logotypami** — masz dwóch testerów. Sekcja z dwoma nazwiskami wygląda gorzej
  niż jej brak. Wraca po pilotażu, z prawdziwymi cytatami i za zgodą — tak jak u nich (GWLEX,
  PragmatIQ z imienia i roli).
- **„13 wyspecjalizowanych baz"** — liczenie baz to ich sposób na pokazanie zasięgu. U nas trzy
  źródła (ISAP, EUR-Lex, SAOS); liczba zabrzmiałaby jak przyznanie się do braku.

---

## 9. Kolejność, gdyby robić po kawałku

1. **Placeholdery** (§0) — bez tego strona się nie nadaje do pokazania nikomu.
2. **Hero** (§2) — jedno zdanie, największy zwrot.
3. **Pasek liczb** (§3) — po przeliczeniu liczb na produkcji.
4. **FAQ** (§5) i **CTA końcowe** (§7) — czysty tekst, zero ryzyka.
5. **„Dla kogo"** (§4) — najwięcej pisania, najmniej pilne.
6. **Poprawki punktowe** (§6) — przy okazji którejkolwiek z powyższych.

⚠️ **Warunek przekrojowy:** żadne zdanie mówiące, że **modele** stoją w UE, nie idzie na produkcję
przed przełączeniem na CloudFerro/Sherlock. Dotyczy trzech miejsc naraz: eyebrow w hero (§2),
odpowiedź „Co się dzieje z moimi pytaniami" w FAQ (§5) i karta „Twoje sprawy zostają Twoje" (§6).
Do tego czasu wszędzie mówimy o **danych** w UE, nie o modelach.
