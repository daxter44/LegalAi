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
„Paweł Ostry" — to konwencja kazusów, nie dane rzeczywistych osób):

- Kazus o umowie sprzedaży drewna przez nieuprawnionego przedstawiciela spółki (prawo cywilne/
  spółki — pełnomocnictwo, ważność umowy).
- Kazus o podziale majątku po rozwodzie i darowiźnie otrzymanej w trakcie małżeństwa (prawo
  rodzinne — art. 31 § 1, 56 § 1, 58 § 3 k.r.o.).
- Kazus o przestoju w zakładzie drzewnym i wynagrodzeniu za czas niewykonywania pracy (prawo pracy).

Pasują do `PLAN-TRYB-KAZUSU.md` (jeśli ten tryb istnieje w produkcie) bardziej niż do prostego
golden-setu pytanie→artykuł. Zostawiam jako materiał, nie duplikuję ich tutaj w pełnej treści — są
w `db-questions-numbered.txt` (scratchpad) pod numerami 73, 85, 67.

## 4. ⚠ Realne, wrażliwe sprawy osobiste — NIE kopiować do repo bez decyzji

Kilka wiadomości to najwyraźniej **prawdziwe, osobiste sprawy realnych użytkowników** (pierwsza
osoba, konkretne kwoty, konkretne daty, konkretne okoliczności rodzinne/zdrowotne/majątkowe) — nie
kazusy egzaminacyjne. Świetny materiał testowy (dokładnie to, do czego produkt ma służyć), ale
**celowo NIE wkleiłem ich pełnej treści do tego dokumentu** i nie umieszczałbym ich w
`golden-set.json` w wersji dosłownej bez Twojej wyraźnej zgody — plik trafia do gita:

1. Spadek odrzucony bez zachowania kolejności — spór rodzinny o dziedziczenie.
2. Rozwód, rozdzielność majątkowa, spór o rozliczenia z małżonkiem.
3. Pogryzienie przez psa, zgłoszenie na policji, pytanie o dalsze kroki.
4. Zatrzymanie i 180 dni w areszcie przez zagubione pismo sądowe — pytanie o odszkodowanie.
5. Alimenty — analiza kosztów utrzymania dziecka, upadłość, depresja, dochody netto.
6. Podejrzenie inwigilacji danych w PUE ZUS przez żonę pracodawcy-lekarkę — najbardziej wrażliwa
   (dane medyczne, potencjalne zniesławienie, tożsamość pracodawcy dająca się namierzyć w połączeniu
   z KRS).
7. Spór z deweloperem o wypłatę 80% wynagrodzenia przy niedokończonych pracach.

**Rekomendacja:** jeśli chcesz je wykorzystać, albo (a) sparafrazuj/zanonimizuj fakty zachowując
strukturę prawną pytania (tak jak zrobiono dla istniejących pozycji golden-setu — żadna nie ma
prawdziwych nazwisk), albo (b) trzymaj je poza gitem (osobny plik nieśledzony, `--refusals` już i
tak czyta je live z bazy bez kopiowania do repo). Nie kopiuję ich treści nigdzie indziej w tym
dokumencie.

## 5. Rekomendacja kolejnego kroku

To jest surowy materiał do wyboru, nie gotowa lista do wklejenia. Zanim cokolwiek trafi do
`golden-set.json`, każda pozycja z sekcji 1–2 potrzebuje: znalezienia właściwego aktu/artykułu w
tekście źródłowym (jak `ue-*` — „zweryfikowane w tekście z CELLAR-a", nie wpisane z pamięci) i
przypisania kategorii (`InCorpus`/`Trap`/`Freshness`/itd.). Proponuję zacząć od sekcji 1 (OKI —
znany, realny problem z tej sesji) i pary „aplikant adwokacki/radcy" (tania, bo to prosta asymetria
reguł, łatwa do zweryfikowania).
