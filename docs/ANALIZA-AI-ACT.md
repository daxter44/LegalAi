# Zgodność PrawoRAG z AI Act

Data: 2026-08-27. Branch: `feat/halfvec-retriever`.

Analiza inżynierska, **nie opinia prawna**. Przed wejściem na rynek warto potwierdzić ją u prawnika
od compliance — przy produkcie sprzedawanym prawnikom to zresztą dobra inwestycja marketingowa.

## Jak czytać ten dokument

Każde twierdzenie ma etykietę:

- **[przepis]** — wynika wprost z rozporządzenia (AI Act, rozp. 2024/1689). Podany artykuł.
- **[ustalone z kodu]** — sprawdzone w repozytorium, ze wskazaniem pliku.
- **[decyzja]** — nasz wybór projektowy, który trzeba utrzymać, żeby analiza pozostała prawdziwa.
- **[do weryfikacji]** — czegoś nie wiem albo stan prawny mógł się zmienić. Wprost, bez zgadywania.

Zero zdań bez pokrycia. Jeśli czegoś nie ustaliłem, jest to napisane.

---

## 1. Podsumowanie dla niecierpliwych

Jesteśmy zgodni, bo **prawie żaden ciężki obowiązek AI Act nas nie dotyczy**. Do zrobienia są trzy
rzeczy, z czego jedna wymaga kodu:

| # | Rzecz | Koszt | Stan |
|---|---|---|---|
| 1 | Jawna deklaracja przeznaczenia systemu (jedno zdanie w dokumentacji) | trywialny | **do zrobienia** |
| 2 | Dowód zapoznania testera z materiałem o systemie (art. 4) | jedno pole w bazie | **do zrobienia** |
| 3 | Oznaczanie treści generowanej, odczytywalne maszynowo (art. 50 ust. 2) | kilkadziesiąt linii + migracja | **do zrobienia** |

Poza AI Act: **tajemnica zawodowa i RODO uderzą wcześniej i mocniej** niż AI Act — patrz sekcja 8.

---

## 2. Jaką rolę pełnimy

**[przepis, art. 3]** Jesteśmy:

- **dostawcą systemu AI** — budujemy PrawoRAG i udostępniamy go pod własną nazwą;
- **stosującym** cudzy model ogólnego przeznaczenia (GPAI) — Gemma za cudzym API.

**Nie jesteśmy dostawcą modelu GPAI** — nie trenujemy modeli. Cały rozdział V (dokumentacja modelu,
polityka praw autorskich, streszczenie danych treningowych, ryzyko systemowe) nas omija.

**[decyzja]** To przestaje być prawdą, jeśli **dostroimy Gemmę (fine-tuning)** na korpusie prawnym.
Wtedy możemy stać się dostawcą zmodyfikowanego modelu GPAI i przejąć obowiązki z rozdziału V dla tej
modyfikacji. Na dziś fine-tuningu nie planujemy — ale warto o tym wiedzieć, **zanim** ktoś odpali
pierwszy trening „na próbę".

---

## 3. Praktyki zakazane (art. 5) — czysto

**[przepis]** Brak scoringu społecznego, biometrii, rozpoznawania emocji, technik manipulacyjnych.
Zero ekspozycji. Nie wymaga działania.

---

## 4. Wysokie ryzyko — NIE, ale zależy to od tego, komu sprzedamy

**[przepis, załącznik III pkt 8 lit. a]** Wysokie ryzyko obejmuje systemy AI przeznaczone do
stosowania **przez organ wymiaru sprawiedliwości lub w jego imieniu** do wspomagania w badaniu
i interpretacji faktów i prawa oraz stosowaniu prawa do konkretnego stanu faktycznego.

To jest dokładny opis tego, co robi PrawoRAG — **z jednym wyjątkiem: adresatem są kancelarie, nie
sądy**. Prawnik korzystający z narzędzia we własnej praktyce nie jest organem wymiaru
sprawiedliwości, więc system pozostaje poza wysokim ryzykiem.

### Co to wywraca

- **Sprzedaż lub pilotaż w sądzie, NSA, prokuraturze, ministerstwie** — albo w podmiocie działającym
  *na ich zlecenie*. System staje się wysokiego ryzyka i wchodzi pełen pakiet: system zarządzania
  jakością, dokumentacja techniczna, logowanie zdarzeń, nadzór człowieka, ocena zgodności, CE, wpis
  do bazy UE. To projekt na kwartały, nie na tydzień.
- **Funkcja oceniająca ludzi** — analiza dokumentów rekrutacyjnych, zdolności kredytowej, uprawnień
  do świadczeń. To inne punkty załącznika III, ten sam skutek.

### Działanie

**[decyzja]** Wpisać do dokumentacji wdrożeniowej jawne **przeznaczenie (intended purpose)**:

> PrawoRAG jest narzędziem wspomagającym research prawny, przeznaczonym dla profesjonalnych
> pełnomocników (adwokatów, radców prawnych) w ich własnej praktyce. System **nie jest przeznaczony
> do stosowania przez organy wymiaru sprawiedliwości ani w ich imieniu**, ani do oceny osób
> fizycznych. Odpowiedź systemu jest materiałem wyjściowym do weryfikacji przy źródle, nie poradą
> prawną ani podstawą decyzji.

Deklarowane przeznaczenie jest w AI Act podstawą klasyfikacji. To jedno zdanie realnie trzyma nas
poza wysokim ryzykiem, a jego brak zostawia sprawę otwartą.

---

## 5. Przejrzystość (art. 50) — dwa różne obowiązki

Łatwo je pomylić, a to jest sedno tego dokumentu:

| | Adresat informacji | Czego wymaga | Stan |
|---|---|---|---|
| **ust. 1** | człowiek | ma wiedzieć, że rozmawia z AI | **mamy** |
| **ust. 2** | maszyna | treść oznaczona **w formacie odczytywalnym maszynowo** | **nie mamy** |

### 5.1. Ustęp 1 — mamy

**[ustalone z kodu]** `src/PrawoRAG.Api/Components/Pages/OSystemie.razor` tłumaczy wprost, czym jest
system, co umie, czego nie umie i że odpowiedź to „wstępny research, nie porada prawna". Czat AI, do
którego wchodzi się przez tę stronę, spełnia obowiązek z nawiązką.

**[decyzja]** Utrzymać widoczną etykietę przy samej odpowiedzi (patrz 6.4) — nie tylko na stronie
informacyjnej.

### 5.2. Ustęp 2 — obowiązek jest nasz, nie dostawcy modelu

**[przepis, art. 50 ust. 2]** Przepis mówi o „dostawcach systemów AI, **w tym systemów AI ogólnego
przeznaczenia**, generujących syntetyczne treści (…) lub tekst". Zwrot „w tym" **rozszerza** krąg
adresatów, a nie wskazuje jednego odpowiedzialnego.

Rozstrzygnięcie częstego nieporozumienia: **korzystanie z cudzego modelu nie przenosi obowiązku na
jego dostawcę.** Gdybyśmy używali Claude, Anthropic byłby adresatem jako dostawca swojego systemu —
a my jednocześnie jako dostawca swojego. Obowiązki się nakładają, nie wykluczają.

- **[przepis]** AI Act zna mechanikę przenoszenia odpowiedzialności w łańcuchu — art. 25 opisuje,
  kiedy podmiot niżej w łańcuchu staje się dostawcą systemu wysokiego ryzyka. W art. 50 **nie ma nic
  analogicznego**: żadnego zwolnienia dla dostawcy opierającego się na oznaczeniu zrobionym wyżej.
- Argument praktyczny, niezależny od brzmienia przepisu: obowiązek dotyczy „wyników **systemu AI**",
  a naszym wyjściem nie jest surowy tekst modelu. To złożony artefakt — treść + panel źródeł +
  wynik walidacji cytatów, albo raport z werdyktami per paragraf. Dostawca modelu tego nie widzi
  i nie ma czego oznaczyć. Jesteśmy jedynym punktem w łańcuchu, w którym da się to zrobić.

To samo dotyczy hostingu: **OVH czy CloudFerro niczego tu nie przesuwają.** Mogą mieć własne
obowiązki jako udostępniający model, ale nasze zostają nasze.

**[przepis, art. 99 ust. 4]** Naruszenie art. 50 to osobna kategoria kar — do 15 mln EUR albo 3%
światowego obrotu.

### 5.3. Nie liczy się jako oznaczenie

Numerki `[1]`, `[2]` i znacznik „✓ cytaty zgodne" **nie są** oznaczeniem pochodzenia. Mówią „ta teza
ma źródło", a nie „ten tekst wygenerowała maszyna". To informacja o ugruntowaniu, nie o pochodzeniu.

---

## 6. Jak oznaczamy treść generowaną — projekt

### 6.0. Zasada i uzasadnienie wykonalności

**[ustalone z kodu]** Model chodzi za **cudzym API zgodnym z OpenAI**
(`src/PrawoRAG.Llm/OpenAiCompatibleLlmProvider.cs`, `LocalLlmOptions`). Docelowo Gemma u OVH lub
CloudFerro — **nie hostujemy sami**.

**[przepis, art. 50 ust. 2]** Rozwiązania mają być skuteczne i interoperacyjne **„w zakresie,
w jakim jest to technicznie wykonalne"**, z uwzględnieniem stanu techniki.

**[decyzja]** Oznaczamy na poziomie **koperty** (metadanych wokół treści), nie w samym tekście.
Uzasadnienie w sekcji 6.5 — jest częścią zgodności, nie dopiskiem.

### 6.1. Strumień odpowiedzi: zdarzenie `provenance`

Jedno zdarzenie, emitowane **raz, przed pierwszym tokenem**:

```
event: provenance
data: {"aiGenerated":true,"model":"gemma-3-27b-it","hosting":"ovh",
       "system":"PrawoRAG/1.4.2","generatedAt":"2026-08-27T09:12:04Z",
       "grounded":true}
```

- **Dlaczego przed tokenami, nie na końcu**: konsument, który urwie strumień w połowie, i tak musi
  dostać oznaczenie.
- **[ustalone z kodu]** `DoneEvent` (`src/PrawoRAG.Api/Services/ChatEvents.cs`) już niesie `Model` —
  zostawiamy bez zmian. To diagnostyka na końcu, nie deklaracja pochodzenia: przychodzi za późno
  i nie mówi „to jest treść wygenerowana", tylko „użyto tego modelu".
- **[ustalone z kodu]** Odpowiednik jako `ProvenanceEvent` w `ChatEvents.cs`. Repozytorium ma
  udokumentowany parytet toru in-process (Blazor) i SSE (`Program.cs`, `/api/chat`) — oznaczenie
  musi iść oboma tak samo, inaczej powstanie ścieżka bez niego.
- Pole `grounded` nie wynika z AI Act (mamy już `NoRetrievalEvent` dla odpowiedzi bez źródeł), ale to
  naturalne miejsce, a dla odbiorcy ta informacja jest ważniejsza niż nazwa modelu.

### 6.2. Strona HTML: atrybuty na kontenerze odpowiedzi

```html
<div class="msg-assistant"
     data-ai-generated="true"
     data-ai-model="gemma-3-27b-it"
     data-ai-generated-at="2026-08-27T09:12:04Z">
```

W `Chat.razor` przy odpowiedzi i w `Analiza.razor` przy raporcie. To widzi skrypt, wtyczka albo
crawler patrzący na stronę — czyli „odczytywalny maszynowo" w warstwie, którą użytkownik ogląda.

### 6.3. Rekordy trwałe: kolumny w bazie

Tabela `messages` i tabela raportów analiz: `AiGenerated`, `Model`, `GeneratedAt`.
**[ustalone z kodu]** Precedens: `messages.Route` (`ChatRoutes` w `ChatEvents.cs`) — ten sam wzorzec.

Powód jest praktyczny, nie formalny: **odpowiedź odtworzona z historii musi nieść to samo oznaczenie
co świeża.** Bez tego oznaczenie znika po odświeżeniu strony i zostaje tylko przy pierwszym
wyświetleniu — czyli tam, gdzie i tak było najbardziej oczywiste. Raporty z „Analizy dokumentów"
żyją do 6 miesięcy (`RetentionService`) i wracają z listy „Moje analizy", więc dotyczy ich to tym
bardziej.

### 6.4. Warstwa dla człowieka: trwała etykieta przy odpowiedzi

Art. 50 ust. 1, nie ust. 2 — ale domyka temat i kosztuje zdanie. Nie tylko na stronie „O systemie":
przy samej odpowiedzi, nieusuwalnie.

**[ustalone z kodu]** Komentarz przy `NoRetrievalEvent` formułuje dokładnie tę zasadę („UI musi to
pokazać JAWNIE i nieusuwalnie"). Tu obowiązuje ta sama.

### 6.5. Czego świadomie NIE robimy

**[decyzja]** To nie jest formalność. „W zakresie technicznie wykonalnym" jest **oceną**, a ocena
nieudokumentowana w praktyce nie istnieje — przy audycie albo pytaniu od kancelarii liczy się
notatka z datą, nie pamięć zespołu.

**Znak wodny w tekście** (watermarking — statystyczny ślad wplatany w wybór słów przy generowaniu,
np. SynthID-Text) — **odrzucony**:

1. Wymaga kontroli nad dekodowaniem modelu (procesor logitów). Przez cudze API zgodne z OpenAI
   dostępu do logitów nie ma i nie będzie, dopóki nie hostujemy sami.
2. Niezależnie od tego byłby nieskuteczny na naszej treści: odpowiedzi są w dużej części dosłownymi
   cytatami przepisów, a przy cytacie model nie ma swobody doboru słów — nie ma gdzie ukryć sygnału.
   Znak byłby najsłabszy dokładnie tam, gdzie nasze wyjście jest najbardziej charakterystyczne.

**Uwaga na przyszłość:** punkt 1 znika, jeśli kiedyś postawimy własny stos serwujący
(vLLM/TGI/llama.cpp). **Samodzielny hosting modelu o otwartych wagach zwiększa ekspozycję, nie
zmniejsza** — obrona „technicznie niewykonalne" traci wtedy pierwszą nogę.

**C2PA / metadane pliku** — bezprzedmiotowe, dopóki nie ma eksportu do PDF/DOCX.
**[ustalone z kodu]** Dziś ścieżki eksportu do pliku nie ma; PDF występuje wyłącznie jako
*wejście* (`PdfAttachmentExtractor`).

### 6.6. Co wyzwala rewizję tej sekcji

- powstanie eksportu do pliku (PDF/DOCX) → oznaczenie do metadanych pliku (XMP / właściwości
  dokumentu / C2PA); zaprojektować **razem z eksportem**, nie doklejać potem;
- przejście na własny hosting modelu → wraca temat znaku wodnego;
- fine-tuning Gemmy → patrz sekcja 2 (możliwe wejście w rozdział V);
- przyjęcie unijnego kodeksu praktyk dot. oznaczania → patrz sekcja 9.

### 6.7. Czego nasz obowiązek NIE obejmuje

- **Co prawnik zrobi dalej z odpowiedzią.** Skopiuje do memo — oznaczenie znika i to jest w porządku,
  obowiązek dotyczy wyjścia z naszego systemu.
- **[przepis, art. 50 ust. 4]** Jeśli ten prawnik opublikuje wygenerowany tekst jako materiał
  informujący opinię publiczną w sprawie interesu publicznego, obowiązek ujawnienia jest jego, nie nasz.
- **Treści źródłowe** — przepisy i orzeczenia nie są generowane, nie oznacza się ich.

---

## 7. Kompetencje w zakresie AI (art. 4) — jedyny obowiązek, który działa dziś

**[przepis, art. 4]** Obowiązuje od lutego 2025, dotyczy dostawców i stosujących: trzeba zapewnić
„wystarczający poziom kompetencji AI" u osób obsługujących system w naszym imieniu.

Przy pilotażu z prawnikami-testerami to jest tanie. **[ustalone z kodu]** Strona `OSystemie.razor`
*już jest* materiałem szkoleniowym: co system umie, czego nie umie, że bywa nadostrożny, jak czytać
znacznik cytatów, że trzeba weryfikować przy oryginale.

Brakuje **dowodu**: potwierdzenia przy zaproszeniu, że tester ją przeczytał, z datą. Jedno pole
w bazie przy bramce dostępu.

---

## 8. Poza AI Act — to uderzy wcześniej i mocniej

### 8.1. Tajemnica zawodowa

Prośba na stronie „O systemie", żeby nie wpisywać danych klientów, jest dobra — ale funkcja
załączania PDF-ów z umowami i pismami zaprasza dokładnie do tego.

**[ustalone z kodu]** Sam załącznik nie jest zapisywany, ale **treść pytania i raport z analizy
zostają w bazie 6 miesięcy** (`RetentionService`, `MaxAge = 183 dni`).

Dla prawnika to nie jest kwestia zgodności produktu, tylko jego własnej odpowiedzialności
dyscyplinarnej — i pierwsze pytanie, jakie zada partner kancelarii.

### 8.2. RODO

Logi rozmów jako dane osobowe, powierzenie przetwarzania dostawcy modelu, podstawa prawna dla
przetwarzania w celu „poprawy jakości", polityka prywatności, umowa powierzenia. Retencja 6 miesięcy
jest świadoma i udokumentowana w kodzie, ale sama tematu nie zamyka.

### 8.3. Dlaczego OSS + hosting w UE rozbraja oba naraz

Rezygnacja z modeli spoza UE usuwa transfer treści pytań i dokumentów klientów poza EOG. To realna
wygrana — tylko dotyczy **innego reżimu niż AI Act** (art. 50 zostaje nasz tak czy inaczej,
patrz 5.2).

**[decyzja]** Argument za tym, żeby zrobić to **przed** pilotażem z realną kancelarią, nie po.

### 8.4. Licencja Gemmy

Gemma nie jest modelem otwartoźródłowym w sensie licencyjnym — to własne warunki Google (Gemma Terms
of Use) z polityką zakazanych zastosowań, którą trzeba przekazywać dalej. Dla AI Act bez znaczenia
(wyjątek dla wolnego oprogramowania i tak nie objąłby art. 50 ani nas jako dostawcy komercyjnego
systemu), ale przy due diligence kancelarii ktoś to sprawdzi.

---

## 9. Do weryfikacji u źródła

**[do weryfikacji]** Wiedza autora analizy sięga maja 2026. Trzy rzeczy trzeba sprawdzić, zanim
cokolwiek na tym oprzemy:

1. **Unijny kodeks praktyk dot. oznaczania i wykrywania treści generowanych.** Komisja nad nim
   pracowała; miał doprecyzować, co znaczy „technicznie wykonalne" dla tekstu i jakie formaty
   metadanych są uznane za interoperacyjne. Jeśli został przyjęty, **format pól z sekcji 6.1 ma iść
   za nim**, a nie za tą propozycją — przepis mówi o rozwiązaniach *interoperacyjnych*, a własny
   format `data-ai-*` interoperacyjny nie jest. Źródło: strony AI Office.
2. **Czy terminy dla załącznika III faktycznie weszły 2 sierpnia 2026.** Pod koniec 2025 Komisja
   zaproponowała w pakiecie „digital omnibus" przesunięcie części obowiązków dla systemów wysokiego
   ryzyka. Nie wiem, jak to się skończyło. Dla nas ma znaczenie tylko w scenariuszu sprzedaży do
   sądu (sekcja 4) — ale wtedy kluczowe.
3. **Polska ustawa wdrożeniowa** — kto jest organem nadzoru rynku i jakie są krajowe procedury
   zgłoszeniowe. Stanu na dziś nie znam.

---

## 10. Lista do odhaczenia

- [ ] Deklaracja przeznaczenia w dokumentacji wdrożeniowej (sekcja 4)
- [ ] Potwierdzenie zapoznania testera z „O systemie", z datą, przy bramce dostępu (sekcja 7)
- [ ] `ProvenanceEvent` + `event: provenance` w SSE, przed pierwszym tokenem (6.1)
- [ ] Atrybuty `data-ai-*` w `Chat.razor` i `Analiza.razor` (6.2)
- [ ] Kolumny `AiGenerated` / `Model` / `GeneratedAt` + odtwarzanie oznaczenia z historii (6.3)
- [ ] Trwała, nieusuwalna etykieta przy odpowiedzi w UI (6.4)
- [ ] Sprawdzenie kodeksu praktyk KE **przed** zamrożeniem formatu pól (9.1)
- [ ] Warunek zabicia: gdyby klientem miał być sąd/organ — analiza od nowa, to inny reżim (sekcja 4)
