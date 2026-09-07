# Kandydaci do golden-setu z realnych rozmów (tabela `messages`, 2026-08-27)

Źródło: `SELECT DISTINCT trim("Content") FROM messages WHERE "Role"='user'` na bazie `.11`.
**221 wiadomości user, 123 unikalnych treści** po samym trim+distinct. Poniżej ręczna kuracja —
automatyczne fingerprintowanie złapało tylko warianty interpunkcyjne (8 klastrów), resztę bliskich
duplikatów (to samo pytanie innymi słowami) trzeba było rozpoznać czytając treść.

**Nic z tego NIE zostało jeszcze wpisane do `golden-set.json`** — dodanie wymaga przypisania
`expectedEli`/`expectedArticle` zweryfikowanego w tekście źródłowym (tak jak zespół zrobił dla
pozycji `ue-*`), a to praca merytoryczna, nie coś do zgadnięcia. Poniżej surowy materiał do dalszej
kuracji + rekomendacja priorytetów.

## 0. Odrzucone bez dyskusji (śmieci / meta / niesamodzielne)

- Powitania/testy: „siema" (×9), „czesc", „test", „działasz ?", „jeszcze raz", „Wyślę Ci pytanie w
  dwóch częściach", „nie no pracownik jest na umowie o prace" (fragment odpowiedzi, nie pytanie).
- Czysta anafora bez własnej treści (wymaga poprzedniej tury): „Czy są od tego jakieś wyjątki?",
  „A kim jest osoba uprawniona z powyższej odpowiedzi?" / „A kto jest osobą uprawnioną w powyższym
  kontekście?" (**to dosłownie przykład z `FollowUpQuery.cs`/`DIAGNOZA-FOLLOWUP-KOTWICE-SYGNATURA` —
  artefakt testów deweloperskich, nie organiczne pytanie użytkownika**), „Które z tych orzeczeń…",
  „Czy widzisz w bazie apelacje…", „A w kontekście prawa karnego?", „Piszesz z perspektywy lekarza…",
  „A przestępstwo nieudzielenia pomocy?", „A gdybyś miał oszacować…", „A jakiś nowszy wyrok?…",
  „Podaj wyrok o bardzo podobnym stanie faktycznym." (bez podanego stanu), „Kto jest organem
  właściwym lub nadzorującym?" (bez tematu), „co należy zrobić… żeby zamienić na rentę?" (bez
  kontekstu), „Podaj najbardziej zbliżone orzeczenie… nie można podlegać pod dwa systemy" (odnosi
  się do nieznanego wcześniejszego stanu).
- Wymaga załączonego dokumentu, którego nie mamy: „Z perspektywy kontraktora czy ta umowa…" (×2),
  „Czy kaucja określona w tej umowie…", „Czy zgodnie z tą umową wynajmujący…", „Jaka jest wysokość
  kaucji… według tej umowy?", „Przeanalizuj to postanowienie…" (×2), „Czy w załączonej umowie…".
- Kontynuacje kazusu z innej wiadomości (bez sensu poza kontekstem): „Ok to co teraz może zrobić
  Paweł Ostry?", „Ok wiemy już że prawo stoi po jego stronie…".

## 1. Znany incydent tej sesji — PRIORYTET do formalizacji

**Limit wpłat na OKI (Osobiste Konto Inwestycyjne) — 6 wariantów tego samego pytania**, w tym ten
sam ciąg pytanie→doprecyzowanie, który diagnozowaliśmy wcześniej w tej rozmowie („napewno jest w tej
ustawie odpowiedz… przeczytaj ją jeszcze raz"). To nie hipotetyczny przypadek testowy — to realne,
powtarzające się pytanie użytkowników, które dziś kończy się złą odpowiedzią (zły artykuł tego
samego aktu). **Reprezentant do golden-setu:**

> „Jaki jest limit wpłat na OKI (Osobiste Konto Inwestycyjne) oraz od kiedy zaczyna obowiązywać?"

Wymaga: znalezienia właściwego artykułu ustawy o OKI z limitem, zweryfikowanego w tekście źródłowym
(dokładnie ten krok, którego brakowało, żeby to poprawnie zdiagnozować — patrz wcześniejsza część
tej rozmowy o retrievalu OKI).

## 2. Dobre, samodzielne pytania — gotowe do formalizacji (po weryfikacji `expectedEli`)

Pogrupowane tematycznie, zdeduplikowane (jeden reprezentant na temat):

**Prawo pracy / B2B:**
- „Kiedy mogę zwolnić pracownika w trybie natychmiastowym oraz jaka podstawa prawna mnie do tego
  upoważnia?"
- „Pod jakimi warunkami Państwowa Inspekcja Pracy może zmusić przedsiębiorcę na B2B do przejścia na
  umowę o pracę?" (2 warianty tej samej sprawy)
- „Jak ewoluowało pojęcie »pracowniczego podporządkowania« w kontekście umów B2B — linia orzecznicza
  SN 2015–2024?" (analityczne, trudniejsze — dobry test głębi, nie tylko faktu)
- „Co to jest umowa parasolowa?"
- „Pracownicy samorządowi — jak są wynagradzani za nadgodziny?"
- „Reforma [lipiec 2026] dała PIP dodatkowe uprawnienia — co się zmieniło?" **(kandydat na kategorię
  Freshness — świeża zmiana, dobry test aktualności)**

**Administracyjne / lokalne:**
- „Czy przedszkola/żłobki muszą dokonywać opłaty za abonament RTV?" (jeden reprezentant, 5+ wariantów
  w danych)
- „Podawanie leku ratującego życie w przedszkolu — czy nauczyciel ma taki obowiązek?"
- „Czy jest możliwe przekazanie Centrum Usług Wspólnych zadania rozliczeń międzygminnych?"
- „Jakie zgody są potrzebne do oddania w dzierżawę nieruchomości, którą jednostka budżetowa
  dysponuje w ramach trwałego zarządu?"
- „Czym jest centralny rejestr umów? Kto go stosuje? Od kiedy obowiązuje?"

**Podatki / ceny / KSeF:**
- „Jak po 1 stycznia 2025 r. kwalifikować obiekty do podatku od nieruchomości — budowla czy
  budynek?" **(znany dev-diagnostic, DIAGNOZA-TOR-STRUKTURALNY-ART-1A — już częściowo pokryte)**
- „Jak prawidłowo oznaczyć najniższą cenę z ostatnich 30 dni? Kto jest zobowiązany do oznaczania?"
  (dyrektywa Omnibus)
- „Co jeżeli sklep odzieżowy od 30 dni nie zmieniał cen i nie wprowadzał promocji?" (edge case tej
  samej ustawy)
- „Kogo obejmuje obowiązkowy KSeF w 2026 i co oznacza okres przejściowy?"

**Ochrona danych osobowych:**
- „Co grozi za wyciek danych osobowych z systemów medycznych?"
- „Jakie elementy muszą się pojawić w umowie powierzenia przetwarzania danych osobowych zgodnie z
  RODO?"
- „Prowadzę portal, mam politykę prywatności z podstawami z art. 6 RODO — czy mogę wysłać mail o
  nowym portalu do użytkowników?" (samodzielne, ma podaną podstawę w treści)

**Prawo karne / wykroczenia:**
- „Kradzież na kwotę 200 zł — co za to grozi?"
- „Możesz przytoczyć art. 552 § 4 k.p.k.?" (konkretny cytat, dobry test strukturalny)
- „Kim jest osoba uprawniona w kontekście art. 157 § 1 Kodeksu wykroczeń? Chodzi o osobę uprawnioną
  do opuszczenia lasu." **(to dosłownie przykładowa fraza z komentarza w `FollowUpQuery.cs` — artefakt
  własnych testów deweloperskich, warto sformalizować, bo już jest „obciążony" jako przykład w kodzie)**

**Spółki / KRS:**
- „Kto powołuje członków zarządu w spółce z o.o.?"
- „Kto powołuje członków rady nadzorczej w spółce z o.o.?" (INNE pytanie niż powyższe — inny organ)
- „Kto powołuje członków zarządu w spółce komunalnej?" (inny reżim niż zwykła sp. z o.o.)
- „Do kiedy muszą być złożone elektroniczne podpisy członków zarządu na uchwałach zatwierdzających
  sprawozdanie, podpisanych ręcznie przez wspólników?"

**Nieruchomości / sąsiedzkie:**
- „Sąsiad posadził żywopłot z tui 5 m wysokości, gałęzie przechodzą na moją działkę — czy to zgodne
  z prawem?"
- „Czy rękojmia chroni nabywcę nieruchomości, jeśli służebność wpisana w księdze wieczystej wygasła
  przed sprzedażą?"
- „Jakie są sposoby na rozwiązanie umowy dożywocia?"
- „Jakie elementy należy bezwzględnie umieścić w umowie najmu mieszkania?"

**Inne / ciekawe edge case'y:**
- „Czy aplikant adwokacki może zastępować radcę prawnego?" ORAZ osobno „Czy aplikant radcy prawnego
  może zastępować adwokata?" — **to DWIE różne odpowiedzi (asymetria reguł), dobra para kontrolna**
- „Jakie są terminy przedawnienia roszczeń cywilnych?"
- „Kiedy ZUS przyznaje wcześniejszą emeryturę z tytułu pracy w szczególnych warunkach?"
- „Czy można podlegać pod system ubezpieczeń społecznych jednocześnie w Polsce i innym państwie UE?"
  + „Jakieś orzeczenie na podstawie art. 11 ust. 1 i 3 lit. a) rozporządzenia (WE) 883/2004?" (para —
  ta sama regulacja UE, dobra dla korpusu po T1/T2)
- „Czy ktoś się orientuje jak wyglądają przepisy odnośnie prowadzenia punktu do nabijania butli CO2
  do saturatorów? Trzeba mieć zezwolenia?" (niszowe, ale realne pytanie biznesowe)
- „Jak treści generowane przez AI powinny być oznaczane?" **(możliwy overlap z `ue-aiact-*` —
  sprawdzić przed dodaniem, żeby nie dublować)**
- „Jakie zmiany w prawie budowlanym od września 2026 r.?" **(kandydat Freshness)**
- „Przywołaj mi Ustawę z 27 czerwca 1950 r. — Kodeks rodzinny (Dz.U. 1950 nr 34 poz. 308, art. 135,
  135 §1, 138)" **(dobry kandydat na kategorię Trap: ten kodeks został ZASTĄPIONY kodeksem rodzinnym
  i opiekuńczym z 1964 r. — test czy system rozpozna nieaktualny/uchylony akt zamiast pomylić go z
  obowiązującym KRO)**

## 3. Kazusy egzaminacyjne (samodzielne, ale długie) — dobre do trybu kazusu, nie golden-setu 1:1

Trzy rozbudowane stany faktyczne czytają się jak klasyczne polskie kazusy prawnicze (fikcyjne
nazwiska Kowalski/Nowak-owego typu — „Marek Nowak", „Franciszek Nowicki", „Bogusława i Krzysztof K.",
„Paweł Ostry" — to konwencja kazusów, potwierdzone przez autora jako dane testowe):

1. **Sprzedaż drewna przez nieuprawnionego przedstawiciela spółki** — „Marek Nowak, właściciel
   tartaku, sprzedał 1000 m³ drewna Franciszkowi Nowickiemu, który przedstawił się jako prezes spółki
   Dudek S.A. Po zawarciu umowy Nowak sprawdził w KRS, że do reprezentacji upoważniony jest wyłącznie
   Fryderyk Nowicki (ojciec Franciszka). Ceny na rynku drzewnym wzrosły, Nowak mógłby dziś sprzedać
   towar drożej. (1) Czy umowa jest nieważna? (2) Czy Franciszek może twierdzić, że nabył drewno we
   własnym imieniu? (3) Jakie czynności powinien podjąć Nowak — czy może uznać umowę za niewiążącą i
   sprzedać towar komuś innemu?" — pełnomocnictwo rzekome (falsus procurator), art. 103–104 k.c.

2. **Podział majątku po rozwodzie i darowizna w trakcie małżeństwa** — „Bogusława i Krzysztof K. byli
   małżeństwem 19 lat. Krzysztof wniósł o rozwód (art. 56 § 1 k.r.o.). Sąd dokonał podziału majątku
   (art. 58 § 3) i uznał, że dom otrzymany przez Krzysztofa w darowiźnie od babki rok po ślubie
   pozostaje jego własnością — Bogusława mieszkała w nim tylko przez 3 lata. W apelacji Bogusława
   twierdzi, że dom wszedł do majątku wspólnego (art. 31 § 1 k.r.o.), bo został darowany w trakcie
   małżeństwa bez rozdzielności majątkowej. Czy ta argumentacja była zasadna?" — majątek osobisty a
   wspólny, darowizna do majątku osobistego mimo braku rozdzielności.

3. **Przestój w zakładzie drzewnym i wynagrodzenie** — „Paweł Ostry pracuje w zakładzie drzewnym.
   Przez 5 dni nie pracował z powodu usterki w pojazdach dostarczających surowiec. W czasie przestoju
   pracodawca nie powierzył mu innej pracy. Po miesiącu pracodawca odmówił wypłaty wynagrodzenia za
   okres niewykonywania pracy. Proszę ocenić zgodność z prawem działania pracodawcy." — wynagrodzenie
   za przestój niezawiniony przez pracownika, art. 81 k.p.

Pasują do `PLAN-TRYB-KAZUSU.md` (jeśli ten tryb istnieje w produkcie) bardziej niż do prostego
golden-setu pytanie→artykuł, bo ocena poprawnej odpowiedzi wymaga wielostopniowego rozumowania, nie
jednego cytowanego przepisu.

## 4. Dodatkowe kazusy testowe (potwierdzone: fikcyjne/testowe, nie dane realnych osób)

Potwierdzone przez autora (2026-08-27): wszystkie poniższe to dane testowe/wymyślone, nie sprawy
realnych użytkowników — pełna treść, bez redakcji, gotowa jako kandydaci golden-setu (kategoria
`InCorpus`/`needsLawyer:true`, bo ocena poprawności wymaga prawnika, podobnie jak istniejąca pozycja
`lawyer-kredyt-darmowy`):

1. **Odrzucenie spadku bez zachowania kolejności** — „Odrzuciłem u notariusza spadek nie zachowując
   kolejności odrzucania (nie wiedziałem o tym zapisie, notariusz nie poinformował mnie). Dwójka
   moich dzieci odrzuciła spadek w terminie 6 miesięcy po mnie, ale syn tego nie wypełnił, twierdzi,
   że moje odrzucenie się nie liczy, bo kolejność nie została zachowana. Czy moje odrzucenie się
   liczy? Czy moje pozostałe dzieci muszą jeszcze raz odrzucać? Jeśli anuluję odrzucenie, czy będę
   automatycznie obciążony długami spadkowymi?" — prawo spadkowe, art. 1015–1024 k.c.

2. **Rozdzielność majątkowa i rozliczenia rozwodowe** — „Jestem w trakcie rozwodu. Jest zrobiona
   rozdzielność majątkowa 3 lata wstecz. W trakcie tych 3 lat były różne rozliczenia z małżonkiem,
   teraz on kłamie i mówi, że nie wypłaciłam należnej mu sumy. Czy będzie miało to wpływ na podział
   majątku?" — prawo rodzinne, rozliczenia majątkowe.

3. **Pogryzienie przez psa** — „Pies mnie ugryzł podczas spokojnego spaceru chodnikiem. Właścicielka
   nie upilnowała, ten się rzucił i ugryzł mnie w nogę. Zadzwoniłem na 112, przyjechała policja,
   pogotowie. Okazało się, że pies nieszczepiony. Składałem zeznania na policji, więc teoretycznie
   będzie sprawa w sądzie. Co w tej sprawie zrobić?" — odpowiedzialność za zwierzęta, art. 431 k.c.

4. **180 dni w areszcie przez zagubione pismo sądowe** — „Zostałem zatrzymany i osadzony w areszcie
   1 marca 2024 r. Rozprawa miała być 27 czerwca w Żarach. Dowiedziałem się, że sprawy nie ma, bo
   zapomniano wysłać odwołania do sądu. Czekałem w areszcie 180 dni, rozprawa odbyła się dopiero
   29 sierpnia. Jakie są szanse na odszkodowanie?" — odpowiedzialność Skarbu Państwa, niesłuszne
   tymczasowe aresztowanie (rozdział 58 k.p.k.).

5. **Alimenty przy upadłości i depresji rodzica** — „Była żona wykazała koszty utrzymania dziecka na
   7 tys. zł miesięcznie (dziecko zdrowe). Trzy lata temu zbankrutowałem z długami 450 tys. zł,
   pracuję na etacie, mam depresję, po odliczeniu alimentów i zajęć komorniczych zostaje mi 900 zł.
   Co w tej sytuacji?" — obniżenie alimentów, zmiana okoliczności, art. 138 k.r.o.

6. **Podejrzenie nieuprawnionego dostępu do PUE ZUS przez lekarkę-żonę pracodawcy** — „Jestem na L4.
   Na koncie ZUS widzę 4 wiadomości o dostępie lekarza do moich danych bez wystawienia zaświadczenia.
   To żona mojego pracodawcy, stomatolog — nie jestem jej pacjentem (sprawdziłem ze znajomymi z pracy,
   też byli sprawdzani, nikt nie jest pacjentem). (1) Czy to ciężkie naruszenie obowiązków pracodawcy
   uzasadniające rozwiązanie umowy z jego winy? (2) Od czego zacząć — ZUS, UODO, Rzecznik Praw
   Pacjenta, Okręgowa Izba Lekarska, PIP? (3) Jakie roszczenia finansowe mi przysługują?" — ochrona
   danych osobowych (dane medyczne), prawo pracy (rozwiązanie z winy pracodawcy), tajemnica lekarska.

7. **Spór z deweloperem o wypłatę 80%** — „Deweloper stwierdził, że roboty są zakończone i chce 80%
   należności, ale prace wykończeniowe terenu są w trakcie, rusztowania stoją, elewacja niedokończona.
   Deweloper nie chce pokazać wpisu do dzienniczka budowy. Co mogę zrobić? Czy to normalne, że można
   uznać budowę za zakończoną i nadal wykańczać?" — umowa deweloperska, odbiór robót, prawo budowlane.

Dobór artykułu/aktu do `expectedEli` wymaga tej samej weryfikacji co reszta sekcji 2 — te sprawy są
wielowątkowe (często 2–3 gałęzie prawa naraz), więc `needsLawyer:true` (jak `lawyer-kredyt-darmowy`)
jest właściwsze niż twarde `expectedArticle` z jedną poprawną odpowiedzią.

## 5. Rekomendacja kolejnego kroku

To jest surowy materiał do wyboru, nie gotowa lista do wklejenia. Zanim cokolwiek trafi do
`golden-set.json`, każda pozycja z sekcji 1–2 potrzebuje: znalezienia właściwego aktu/artykułu w
tekście źródłowym (jak `ue-*` — „zweryfikowane w tekście z CELLAR-a", nie wpisane z pamięci) i
przypisania kategorii (`InCorpus`/`Trap`/`Freshness`/itd.). Proponuję zacząć od sekcji 1 (OKI —
znany, realny problem z tej sesji) i pary „aplikant adwokacki/radcy" (tania, bo to prosta asymetria
reguł, łatwa do zweryfikowania).
