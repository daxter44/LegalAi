# Analiza dokumentów prawnych (regulamin, polityka prywatności, polityka cookies) vs stan aplikacji

Data: 2026-09-01. Branch: `feat/halfvec-retriever`. Analizowane pliki (od znajomego prawnika):
`Regulamin.txt`, `Polityka prywatności.md`, `Polityka cookies.md`
(`C:\Users\mmarcinkowski\OneDrive - Euvic\Desktop\docs`).

Analiza inżynierska z odniesieniem do przepisów, **nie opinia prawna**. Każde twierdzenie o aplikacji
jest sprawdzone w repozytorium i ma wskazany plik. Twierdzenia prawne oznaczone **[do potwierdzenia
u prawnika]** wymagają jego decyzji.

Etykiety: **[ustalone z kodu]**, **[dokument]** (co mówi dokument), **[luka]** (brak, który nas
osłabia), **[do potwierdzenia u prawnika]**, **[do zrobienia w kodzie]**.

---

## 1. Werdykt

**Szkielet jest dobry, treść w ok. 40% opisuje inny produkt.** Klauzule o charakterze AI („AI sugeruje,
użytkownik decyduje"), ograniczeniu odpowiedzialności, kontach, limitach, zmianach regulaminu i okresie
próbnym są solidne i pasują do OmniaSI. Natomiast opis funkcji, kanałów, dostawców i przepływu danych
został wzięty z produktu, którego nie mamy: aplikacja mobilna, dodatek do Worda, pseudonimizacja
lokalna, Whisper, logowanie Google/Apple, Sentry, publiczne linki do raportów, ekran zgody na AI.

**W tej postaci dokumentów nie wolno opublikować.** Nie dlatego, że za słabo nas chronią, ale dlatego,
że **obiecują zabezpieczenia, których nie ma** (pseudonimizacja dokumentów przed analizą, brak
przekazania danych do AI bez zgody). Obietnica nieistniejącego mechanizmu ochrony danych jest gorsza niż
jej brak: przy incydencie zamienia spór o należytą staranność w spór o wprowadzenie w błąd.

Trzy rzeczy do zrobienia, w tej kolejności:

1. **Wyciąć wszystko, co opisuje nieistniejące funkcje** (sekcja 3). To praca redakcyjna, tania.
2. **Dopisać brakujące elementy ochronne** (sekcja 4): podmiot z adresem, prawo właściwe, tryb
   reklamacji, „usługa wyłącznie dla profesjonalistów", umowa powierzenia, przeznaczenie systemu wg AI Act.
3. **Domknąć w kodzie cztery rzeczy, bez których polityka nie będzie prawdziwa** (sekcja 6).

Model cenowy „7 dni próbne, potem płatne, bez stałego darmowego planu" **mieści się w obecnym pkt 8
regulaminu** bez przeróbek; potrzebne są tylko doprecyzowania opisane w sekcji 5.

---

## 2. Co aplikacja faktycznie robi (fakty do porównania)

Skrót ustaleń z kodu, na których opiera się reszta dokumentu.

| Obszar | Stan faktyczny **[ustalone z kodu]** | Plik |
|---|---|---|
| Kanał | Wyłącznie aplikacja webowa (Blazor Server). Brak aplikacji mobilnej i dodatku do Worda. | `PrawoRAG.slnx`, `src/PrawoRAG.Api` |
| Logowanie | Tylko e-mail + hasło (ASP.NET Identity, PBKDF2). Brak Google/Apple/OAuth. Opcjonalna „nazwa wyświetlana", brak imienia/nazwiska/zdjęcia. | `Program.cs:168`, `Services/Auth/AuthEndpoints.cs:49-114` |
| Sesja | Ciasteczko `praworag.auth`, HttpOnly, 30 dni przesuwane. Brak JWT i localStorage. | `Program.cs:169-186` |
| Zgody | Rejestracja wymaga akceptacji regulaminu; zapisywana data i wersja (`TermsVersion="2026-08"`). Osobny, opcjonalny checkbox marketingowy (`MarketingConsentVersion="2026-09"`). | `AuthEndpoints.cs:71`, `Entities/AppUserEntity.cs` |
| Co trafia do bazy | Pełna treść pytań i odpowiedzi (`messages.Content`), źródła, model; dla analiz: nazwa pliku, liczba stron, polecenie użytkownika, streszczenie i odpowiedzi per paragraf (parafrazują dokument). Konto: e-mail, hash hasła, plan, identyfikatory Stripe. Liczniki zużycia. | `Entities/MessageEntity.cs`, `Entities/AnalysisEntity.cs` |
| Czego NIE ma w bazie | Treści załączonych PDF (tylko RAM procesu, TTL 60 min), adresów IP, ról administratora. | `Services/AnalysisSession.cs`, `Program.cs:230` |
| Retencja | Rozmowy i raporty analiz kasowane automatycznie po 183 dniach. Konto i liczniki zużycia bezterminowo. | `Services/RetentionService.cs:17` |
| Usunięcie konta / eksport | Brak endpointu i procedury. UI mówi „na żądanie". `UserId` to string rozproszony po 3 tabelach bez klucza obcego. | `Services/Billing/BillingPages.cs:215`, `PLAN-KOMERCJALIZACJA.md:83` |
| Publiczny link do raportu | Nie istnieje. Wszystkie odczyty filtrowane po użytkowniku. | `Services/ConversationStore.cs:34` |
| Pseudonimizacja | Nie istnieje. Jest tylko **ostrzeżenie** o PESEL/NIP/REGON/telefonie/e-mailu/IBAN, które nic nie maskuje i nie blokuje wysyłki. Nazwiska celowo pominięte. | `src/PrawoRAG.Domain/Privacy/SensitiveDataDetector.cs` |
| Modele językowe | Domyślnie: lokalny Bielik przez API zgodne z OpenAI. Kod ma też klienta Anthropic (opt-in). Alfa działała na Google AI Studio (USA). **Cel produkcyjny: CloudFerro Sherlock (PL) z Bielik/PLLuM/Gemma.** Brak Whisper i jakiegokolwiek audio. | `src/PrawoRAG.Llm/*`, `docs/RUNBOOK-LLM-PROVIDER.md` |
| Embeddingi, reranker | Własny TEI w sieci lokalnej. Brak dostawcy zewnętrznego. | `src/PrawoRAG.Embeddings/*` |
| Log aplikacji | Jedno miejsce zapisuje **pełną treść pytania** do logu: prośby o przygotowanie pisma (`DRAFTING_REQUEST`). Poza tym tylko identyfikatory. | `Services/ChatService.cs:77` |
| Poczta | Resend (USA) do maili transakcyjnych; wymagany przy włączonych kontach. | `Services/Auth/AppEmailSender.cs` |
| Płatności | Stripe Checkout + portal + webhook, za flagą `Billing:Enabled`. Do Stripe idzie e-mail i id użytkownika. | `Services/Billing/BillingEndpoints.cs:113-116` |
| Analityka | Microsoft Clarity + Google Analytics 4, **domyślnie wyłączone**, ładowane wyłącznie po zgodzie z bannera. Clarity nagrywa sesje, pola nie są maskowane. | `Services/AnalyticsOptions.cs`, `wwwroot/js/consent.js` |
| Monitoring błędów | Brak (Sentry, AppInsights, OTel nie występują). | wszystkie `*.csproj` |
| Ciasteczka | `praworag.auth` (30 dni), antiforgery ASP.NET, `omniasi-consent` (365 dni, JS). Brak localStorage. Brak strony `/cookies`; banner linkuje do `/prywatnosc`. | `Program.cs`, `consent.js:11-19` |
| Źródła korpusu | SAOS (sądy powszechne, SN), ELI/ISAP (api.sejm.gov.pl), EUR-Lex/CELLAR (prawo UE, CC BY 4.0), NSA/WSA z datasetu JuDDGES/pl-nsa (Hugging Face, CC BY 4.0, pochodny CBOSA). **Brak** komentarzy, literatury, Lex/Legalis. | `src/PrawoRAG.Ingestion/*`, `docs/RUNBOOK-INGESTIA-*.md` |
| Funkcje | Czat z cytatami i odmowami, wyszukiwarka orzecznictwa (bez zakładki), analiza dokumentów per paragraf (za flagą), pytania uzupełniające, historia, ocena odpowiedzi, licznik zużycia. **Brak**: eksportu, udostępniania, porównywania wersji, sugestii redakcyjnych, trybu kazusu. | `Components/Pages/*.razor` |
| Oznaczenie AI | Baner „Treść wygenerowana przez AI" + atrybuty `data-ai-*` na każdej odpowiedzi (AI Act art. 50 ust. 2). | `Chat.razor:107-113`, `ChatEvents.cs:43-47` |
| Strony prawne | `/regulamin` i `/prywatnosc` to placeholdery „Dokument w przygotowaniu", a akceptacja już jest wymagana przy rejestracji. Linków do nich nie ma w nagłówku czatu. | `Program.cs:571-587`, `MainLayout.razor` |
| Podmiot, domena, e-mail | Nigdzie nie ustalone. Plan: „JDG czy spółka [decyzja do podjęcia]". Domena celowo nierezerwowana. | `PLAN-KOMERCJALIZACJA.md:177` |

---

## 3. Rozbieżności: dokument mówi X, aplikacja robi Y

Posortowane od najgroźniejszych. Kolumna „Co zrobić" to propozycja redakcyjna; decyzję podejmuje prawnik.

### 3.1. Groźne (obietnica ochrony, której nie ma)

| # | Dokument **[dokument]** | Aplikacja **[ustalone z kodu]** | Co zrobić |
|---|---|---|---|
| R1 | Polityka §4: „lokalne przetwarzanie… pseudonimizacja dokumentów przed dalszą analizą… powiązanie zachowane w postaci zaszyfrowanej po stronie użytkownika". Regulamin pkt 2a i pkt 6: „pseudonimizacja danych osobowych… może odbywać się lokalnie na urządzeniu". | Nie ma żadnej pseudonimizacji ani anonimizacji, ani lokalnie, ani na serwerze. Jest niemaskujące ostrzeżenie o PESEL/NIP itp. Treść idzie do modelu tak, jak ją wpisano. | **Usunąć całą §4 polityki i wzmianki w regulaminie.** Zastąpić prawdą, która sama jest mocna: „treść załączonych dokumentów nie jest zapisywana na dysku ani w bazie; przetwarzana wyłącznie w pamięci na czas analizy (do 60 min)". Dopisać zalecenie: „nie wprowadzaj danych identyfikujących klientów" (jest już w UI, `OSystemie.razor`). |
| R2 | Polityka §5: „Przed pierwszym przekazaniem danych… aplikacja mobilna wyświetla ekran zgody… bez jej udzielenia dane nie są przekazywane." | Brak takiego ekranu. Przekazanie do modelu jest istotą usługi. | Usunąć. Podstawą jest wykonanie umowy (art. 6 ust. 1 lit. b RODO), nie zgoda; zgoda byłaby tu wręcz błędem, bo jej cofnięcie musiałoby zatrzymać usługę. **[do potwierdzenia u prawnika]** |
| R3 | Polityka §5 i §6: dostawcy AI to OpenAI (Whisper), Anthropic (Claude), Google (Gemini). Regulamin pkt 6: „np. Anthropic". Regulamin pkt 3: „modele dostarczane przez podmioty trzecie". | Brak Whisper i audio. Klient Anthropic istnieje w kodzie, ale nie jest celem. Docelowo: model otwarty (Bielik/PLLuM/Gemma) hostowany w CloudFerro (PL/UE). Alfa na Google AI Studio to świadomy wyjątek tylko dla uprzedzonych testerów (`RUNBOOK-LAUNCH-ALFA.md`). | Wpisać docelowego dostawcę hostingu modelu (CloudFerro sp. z o.o., Polska) i nazwę rodziny modeli. Zostawić generyczną furtkę: „inni dostawcy z siedzibą w UE/EOG wskazani w aktualnej wersji polityki". **Przewaga sprzedażowa**: możemy napisać, że treść pytań i dokumentów nie opuszcza EOG. Uwaga: to wymaga, by w produkcji naprawdę nie było Google AI Studio ani OpenRouter. |
| R4 | Polityka cookies: „wyłącznie cookies niezbędne", „nie wykorzystuje cookies analitycznych", „nie korzysta z narzędzi śledzących". Polityka prywatności §6 wymienia jednak „narzędzi analitycznych" jako odbiorcę. | Kod ma Clarity + GA4 za zgodą, domyślnie wyłączone. Clarity nagrywa sesje; na stronie czatu może nagrać treść pytań. | **Decyzja produktowa do podjęcia.** Wariant A (rekomendowany): wyłączyć Clarity/GA na stałe, usunąć z kodu banner; polityka cookies zostaje prawdziwa, a „zero śledzenia" jest argumentem dla kancelarii. Wariant B: zostawić, ale wtedy polityka cookies musi opisać Clarity i GA (usługi USA), zgodę i jej cofanie, a w kodzie trzeba zamaskować pola formularzy dla Clarity. Nie wolno zostawić stanu mieszanego. |
| R5 | Polityka §2.4: logowanie Google Sign-In / Sign in with Apple, imię i nazwisko, zdjęcie profilowe. §6: Google Ireland jako odbiorca. | Tylko e-mail + hasło. Opcjonalna nazwa wyświetlana. | Usunąć. Zostawić: e-mail, hash hasła, opcjonalna nazwa wyświetlana, data akceptacji regulaminu i wersja, zgoda marketingowa. |

### 3.2. Istotne (opis innego produktu)

| # | Dokument | Aplikacja | Co zrobić |
|---|---|---|---|
| R6 | Regulamin pkt 2: „aplikacja internetowa, aplikacja mobilna oraz dodatek (add-in) dla Microsoft Word". Polityka §2.5–2.8: mikrofon, biblioteka zdjęć, lokalna baza na urządzeniu, dodatek Word. | Tylko web. Wejście dokumentów: wyłącznie PDF do 10 MB / 100 stron. | Usunąć. Jeśli prawnik chce zachować furtkę: „usługa dostępna przez aplikację internetową; inne kanały dostępu, o ile zostaną udostępnione, podlegają regulaminowi". |
| R7 | Polityka §2.7 i §6: Sentry (Functional Software, Inc.). | Brak jakiegokolwiek narzędzia monitoringu błędów. | Usunąć. |
| R8 | Regulamin pkt 6: „użytkownik może udostępnić raport za pomocą publicznego linku". | Nie istnieje. | Usunąć. Korzyść uboczna: bez publicznego udostępniania treści nie wchodzimy w obowiązki platformy hostingowej z DSA (rozp. 2022/2065). **[do potwierdzenia u prawnika]** |
| R9 | Regulamin pkt 2a: „porównywanie wersji dokumentów, proponowanie zmian i sugestii redakcyjnych". Pkt 2b: „materiałów doktrynalnych". | Brak porównywania i sugestii. Brak doktryny (strona „O systemie" wprost: „nie ma komentarzy ani literatury"). Jest: analiza umowy per paragraf z werdyktem OK / ryzyko / brak źródeł. | Opisać to, co jest: analiza dokumentu per jednostka redakcyjna z odniesieniem do przepisów i orzeczeń. Wykreślić doktrynę. Zostawić klauzulę o prawie dodawania i wycofywania modułów (już jest). |
| R10 | Regulamin pkt 4: źródła SAOS, CBOSA, ISAP. | SAOS, ELI/ISAP (api.sejm.gov.pl), **EUR-Lex** (prawo UE, brak w regulaminie), NSA/WSA z datasetu **JuDDGES/pl-nsa** (Hugging Face, CC BY 4.0), a nie bezpośrednio CBOSA. | Dodać EUR-Lex/Urząd Publikacji UE i dataset NSA. **[do zrobienia w kodzie]** atrybucja CC BY 4.0 dla pl-nsa i EUR-Lex (wymóg licencji, `RUNBOOK-INGESTIA-NSA.md:31` czeka na prawnika). Pkt 10 „materiały urzędowe" (art. 4 pr. aut.) pasuje do PL; dla EUR-Lex obowiązuje polityka ponownego wykorzystania Komisji, **[do potwierdzenia u prawnika]**. Regulamin SAOS niesprawdzony (`PLAN.md:342`). |
| R11 | Polityka §6: brak Stripe i Resend wśród odbiorców. Regulamin pkt 8: „zewnętrzny operator płatności" (OK). | Stripe (e-mail, id użytkownika, id klienta i subskrypcji) oraz Resend (e-mail, treść maili transakcyjnych). Oba to firmy z USA. | Dopisać jako odbiorców z kategorią danych. W §7 (transfer poza EOG) rozdzielić: **treść pytań i dokumentów: EOG**; dane konta, płatności i e-maile: mogą trafić do USA na podstawie SCC / decyzji o adekwatności (DPF). |
| R12 | Polityka §8: okresy ogólnikowe („przez okres niezbędny"). Regulamin pkt 6: zapytania i raporty „są przechowywane w celu historii". | Konkret: rozmowy i raporty analiz **183 dni**, potem automatyczne usunięcie; konto do usunięcia na żądanie; liczniki zużycia bezterminowo; dane rozliczeniowe wg przepisów podatkowych. | Wpisać konkretne okresy (art. 13 ust. 2 lit. a RODO wymaga okresu lub kryteriów). „6 miesięcy" to zarazem argument dla kancelarii. Dodać zdanie o kopiach zapasowych i ich retencji, jeśli będą. |
| R13 | Polityka §3: cele bez marketingu i bez rozliczeń. | Aplikacja zbiera zgodę marketingową (checkbox, data, wersja) i będzie wystawiać płatności. | Dodać cel marketingowy (art. 6 ust. 1 lit. a RODO + zgoda na e-mail wg prawa komunikacji elektronicznej) z prawem cofnięcia oraz cel rozliczeniowo-księgowy (art. 6 ust. 1 lit. b i c). **[do potwierdzenia u prawnika]** |
| R14 | Polityka §9: prawo do przenoszenia danych, usunięcia. Regulamin pkt 11: „zażądać usunięcia konta, kontaktując się". | Brak endpointu usuwania i eksportu; usuwanie ręczne wymaga skasowania rekordów w kilku tabelach po stringu `UserId`. | Dokumenty mogą zostać („na żądanie, w terminie do 30 dni"), ale **[do zrobienia w kodzie]**: skrypt lub endpoint usuwania konta i prosty eksport (JSON rozmów). Bez tego obietnica z §9 jest niewykonalna w terminie z art. 12 RODO. |

### 3.3. Drobne, ale widoczne

| # | Problem | Co zrobić |
|---|---|---|
| R15 | Polityka cookies mówi o „serwisach TUUL" (dwa razy). Pozostałość z szablonu innego klienta. | Zamienić na OmniaSI. |
| R16 | Pisownia: „OmniaSI" (regulamin) vs „OmniaSi" (polityki). W kodzie: `OmniaSI`. Adresy: `kontakt@OmniaSI.pl` (regulamin, cookies) vs `info@OmniaSi.pl` (polityka). Domena nie jest zarejestrowana (decyzja świadoma). | Ujednolicić na OmniaSI i jeden adres. Wpisać po rezerwacji domeny. |
| R17 | Daty: regulamin „obowiązuje od 16 lipca 2026", polityka „od 28 sierpnia 2026". Kod zapisuje `TermsVersion="2026-08"`. | Ustalić jeden identyfikator wersji i wpisać go do `Auth:TermsVersion` przy publikacji. Data wejścia w życie = data publikacji, nie wcześniejsza. |
| R18 | Cookies: brak listy konkretnych ciasteczek. | Dopisać: `praworag.auth` (sesja, 30 dni), antiforgery ASP.NET (sesja), `omniasi-consent` (12 mies., tylko jeśli zostaje analityka). **[do zrobienia w kodzie]** trasa `/cookies` albo sekcja w `/prywatnosc`; linki do dokumentów prawnych w nagłówku czatu (`MainLayout.razor` ich nie ma). |
| R19 | Polityka §2.1 adres IP. | Aplikacja nie zapisuje IP; zapisze je reverse proxy / Cloudflare. Zapis w polityce jest poprawny jako ogólny, zostawić. |

---

## 4. Czego brakuje, żeby regulamin nas chronił

Regulamin ma mocne wyłączenia odpowiedzialności, ale brakuje w nim elementów, które w Polsce są
wymagane ustawowo albo które decydują o tym, czy te wyłączenia w ogóle zadziałają.

### 4.1. Podmiot i prawo właściwe (najważniejsze)

- **[luka]** Regulamin: „Coding.NET - Marcin Marcinkowski UG z siedzibą w Niemczech", bez adresu,
  numeru rejestru, NIP/VAT. Polska ustawa o świadczeniu usług drogą elektroniczną (art. 5) i niemiecki
  DDG (§5, Impressum) wymagają pełnych danych identyfikujących. Plan komercjalizacji ma to jako
  **otwartą decyzję** (JDG czy spółka, `PLAN-KOMERCJALIZACJA.md:177`). Dopóki nie zapadnie, dokumenty
  nie mogą być finalne: podmiot wpływa na prawo właściwe, VAT, KSeF i konto Stripe.
- **[luka]** Brak wyboru prawa i sądu. Klauzule w pkt 9 („istotne obowiązki umowne", „szkoda typowa
  i przewidywalna") to konstrukcja prawa niemieckiego (Kardinalpflichten). Przy polskim kliencie i braku
  klauzuli wyboru prawa sąd może stosować prawo polskie, gdzie te sformułowania nie mają utartego
  znaczenia. **[do potwierdzenia u prawnika]**: jedno prawo, spójna terminologia, wskazanie sądu.
- **Zalecenie**: wpisać wprost, że **usługa jest kierowana wyłącznie do przedsiębiorców i osób
  wykonujących zawód prawniczy w ramach działalności zawodowej**, a użytkownik to oświadcza przy
  rejestracji. To jedno zdanie decyduje, czy działają: wypowiedzenie bez powodu (pkt 8), limit
  odpowiedzialności (pkt 9), „as is", brak 14-dniowego prawa odstąpienia. Wobec konsumenta większość
  tych klauzul byłaby niedozwolona (art. 385^1 i n. KC, ustawa o prawach konsumenta). **[do potwierdzenia
  u prawnika]** czy dopuszczamy aplikantów i studentów; jeśli tak, potrzebny osobny tryb konsumencki.

### 4.2. Wymagane elementy regulaminu usługi elektronicznej (art. 8 ust. 3 uśude)

| Element | Stan |
|---|---|
| Rodzaje i zakres usług | Jest (pkt 2), do poprawy wg sekcji 3 |
| Wymagania techniczne | **[luka]** Brak. Dopisać: aktualna przeglądarka z JS i ciasteczkami, połączenie z internetem, konto e-mail |
| Zakaz dostarczania treści bezprawnych | Częściowo w pkt 5; dopisać wprost |
| Warunki zawierania i rozwiązywania umowy | Częściowo (pkt 8, 11). Dopisać moment zawarcia umowy (rejestracja + akceptacja + potwierdzenie e-mail), język umowy, formę |
| **Tryb postępowania reklamacyjnego** | **[luka]** Brak w całości. Dopisać: adres, treść reklamacji, termin odpowiedzi (np. 14 dni) |

### 4.3. Ochrona danych klientów kancelarii

- **[luka] Umowa powierzenia (art. 28 RODO).** Gdy prawnik wpisuje dane klienta, dla tych danych
  jesteśmy **podmiotem przetwarzającym**, a nie administratorem. Polityka traktuje wszystko jako
  administrowanie. Standard SaaS dla kancelarii to załącznik „Umowa powierzenia przetwarzania" do
  regulaminu, akceptowany razem z nim. Plan Tor C pkt 2 to przewiduje. Bez tego kancelaria z działem
  compliance nie kupi. **[do potwierdzenia u prawnika]**
- **[luka] Podwykonawcy przetwarzania** (lista w DPA): CloudFerro (model), hosting (Hetzner/Scaleway,
  decyzja niepodjęta, `PLAN-SIZING-DEPLOY-2026-08-24.md`), Resend, Stripe. Zmiana listy z powiadomieniem.
- **Tajemnica zawodowa**: pkt 5 słusznie przenosi odpowiedzialność na użytkownika. Dopisać zalecenie
  „opisuj stan faktyczny bez danych identyfikujących" oraz informację, co jest przechowywane i jak długo
  (spójnie z R12). To pierwsze pytanie partnera kancelarii (`ANALIZA-AI-ACT.md` §8.1).

### 4.4. AI Act

- Regulamin dobrze realizuje przejrzystość wobec człowieka (pkt 2, 3). Aplikacja realizuje oznaczenie
  maszynowe (art. 50 ust. 2). Landing już deklaruje zgodność z AI Act.
- **[luka] Deklaracja przeznaczenia (intended purpose).** Załącznik III pkt 8 lit. a AI Act obejmuje
  systemy „do stosowania przez organ wymiaru sprawiedliwości lub w jego imieniu". Nasz opis funkcji
  pasuje do tego dokładnie, z jednym wyjątkiem: adresatem są pełnomocnicy, nie sądy. Trzeba to napisać
  w regulaminie (pkt 2): *„System jest przeznaczony dla profesjonalnych pełnomocników w ich własnej
  praktyce. Nie jest przeznaczony do stosowania przez organy wymiaru sprawiedliwości ani w ich imieniu,
  ani do oceny osób fizycznych."* Jedno zdanie, które trzyma nas poza kategorią wysokiego ryzyka
  (`ANALIZA-AI-ACT.md` §4). Dodać do pkt 5 zakaz takiego użycia.
- Regulamin pkt 3 pisze „modele dostarczane przez podmioty trzecie". Zostaje prawdziwe (Gemma/Bielik
  to cudze modele), ale przy Gemmie obowiązuje Gemma Terms of Use z polityką zakazanych zastosowań,
  którą trzeba przekazać dalej użytkownikom (`ANALIZA-AI-ACT.md` §8.4). **[do potwierdzenia u prawnika]**
  po wyborze modelu produkcyjnego.

### 4.5. Pozostałe luki

- **Licencja na treści użytkownika**: pkt 6 mówi „użytkownik zachowuje prawa", ale nie udziela nam
  licencji na przetwarzanie treści w celu świadczenia usługi (technicznie: kopiowanie, embedding,
  przekazanie do modelu, przechowywanie 6 mies.). Dopisać ograniczoną, niewyłączną licencję w tym celu.
- **Własność wyników**: brak klauzuli, komu przysługują wygenerowane odpowiedzi i raporty. Standard:
  użytkownik może z nich korzystać bez ograniczeń, my nie roszczymy praw.
- **Siła wyższa**: brak. Pkt 7 częściowo pokrywa („przyczyny niezależne").
- **Zakaz użycia do trenowania konkurencyjnych modeli / masowego pobierania wyników**: pkt 5 ma scraping;
  dopisać wprost budowę zbiorów danych i konkurencyjnych usług.
- **Wiek**: brak wymogu pełnoletności (przy B2B-only wystarczy oświadczenie o działalności zawodowej).
- **Kontakt w sprawie feedbacku / kontakt operacyjny** (przerwy, incydenty): dopisać kanał i zgodę
  domniemaną na komunikaty serwisowe (nie marketingowe).

### 4.6. Co w regulaminie jest dobre i warto zostawić

Pkt 3 (ograniczenia AI, halucynacje, „AI sugeruje, użytkownik decyduje"), pkt 4 (brak wyniku nie
oznacza braku przepisu, opóźnienie aktualizacji), pkt 5 (odpowiedzialność użytkownika, etyka
zawodowa), pkt 8 (automatyczne odnowienie, zmiana cen od kolejnego okresu, wypowiedzenie ze zwrotem
proporcjonalnym), pkt 9 (limit 12 mies. opłat, wyłączenia z zachowaniem winy umyślnej i rażącego
niedbalstwa), pkt 11 (konto osobiste, zakaz multikont pod trial), pkt 12 (14 dni na zmiany, zwrot
przy odejściu), pkt 13 (salwatoryjna). To dokładnie te klauzule, których potrzebuje produkt AI dla
prawników; są sformułowane ostrożnie i z zastrzeżeniem prawa bezwzględnie obowiązującego.

---

## 5. Model cenowy „7 dni próbne, potem płatne" a regulamin

Pkt 8 już mówi: „bezpłatny okres próbny oraz płatne plany subskrypcyjne… zakres i warunki okresu
próbnego określa serwis… może w każdym czasie zmienić warunki okresu próbnego lub go zakończyć".
**To jest wystarczająco generyczne**: obejmuje zarówno wariant „trial, potem płatne, bez darmowego
planu", jak i ewentualny powrót darmowego poziomu. Regulamin nie wymienia ani „free", ani liczb 15/300,
więc przestawienie modelu nie wymaga jego zmiany, tylko cennika w serwisie.

Do doprecyzowania, żeby uniknąć sporów przy trialu:

1. **Czy trial wymaga podania karty i przechodzi automatycznie w płatną subskrypcję.** Jeśli tak
   (tryb `trial_period_days` w Stripe, kod `SubscriptionSync.cs:65` już mapuje status `trialing` na
   dostęp), regulamin i ekran startu triala muszą to mówić wprost, wraz z terminem i kwotą pierwszej
   płatności oraz sposobem anulowania przed końcem. Jeśli nie, dopisać, że po upływie triala dostęp
   jest wstrzymywany do czasu wyboru planu. **Decyzja produktowa, nie prawna.**
2. **Trial jednorazowy per osoba** i per adres e-mail; pkt 11 (zakaz multikont) już to zabezpiecza,
   warto dodać jedno zdanie w pkt 8.
3. **Trial bez gwarancji**: możliwość skrócenia lub zakończenia bez roszczeń (jest w pkt 8, zostawić).
4. **Pula zapytań w planie**: nasz model to limit zapytań na okres rozliczeniowy (chat i analiza liczone
   wspólnie, `PlanLimits.RequestsPerMonth`) z osobnymi dziennymi limitami pojemności (`CostGuard`).
   Pkt 8 opisuje głównie plany „bez puli" z zasadą fair use. Dopisać zdanie: *„Limity planu wskazane
   są w cenniku i w panelu konta; niewykorzystane zapytania nie przechodzą na kolejny okres i nie
   podlegają zwrotowi. Niezależnie od planu operator może stosować dzienne limity bezpieczeństwa."*
   Zasadę fair use zostawić dla przyszłych planów bez puli.
5. **Okres rozliczeniowy** liczony od dnia zakupu, nie od 1. dnia miesiąca (`BillingPeriod.cs`).
   Warto to wpisać, bo użytkownicy zakładają miesiąc kalendarzowy.
6. **Faktury**: regulamin milczy. Dopisać zgodę na faktury elektroniczne i to, kto je wystawia
   (Stripe nie wystawia polskiej faktury; kwestia KSeF otwarta, `PLAN-KOMERCJALIZACJA.md:171`).

---

## 6. Do zrobienia w kodzie, zanim dokumenty będą prawdziwe

Kolejność wg ryzyka. Żadne nie jest duże.

| # | Zadanie | Dlaczego | Gdzie |
|---|---|---|---|
| K1 | ~~Usunąć logowanie pełnej treści pytania~~ **DECYZJA 2026-09-01: zostaje.** Zamiast zmiany kodu polityka prywatności ujawnia logi z treścią pojedynczych zapytań (pkt 2.7) i ich retencję (pkt 8). | Treść pytań w logach żyje poza retencją 6 mies., więc musi być opisana w polityce. | `Services/ChatService.cs:77`, `Legal/polityka-prywatnosci.md` |
| K2 | Usunięcie konta z ustawień: **wg użytkownika (2026-09-01) już zaimplementowane**, ale na gałęzi `feat/halfvec-retriever` ani na żadnej innej w tym repo nie ma endpointu; prawdopodobnie praca niezcommitowana poza tą maszyną. **Eksport historii: DECYZJA 2026-09-01: nie planujemy**, obietnica usunięta z regulaminu (§ 14) i polityki (pkt 9.3). Prawo do przenoszenia (art. 20 RODO) realizowane ręcznie na wniosek. | Polityka §9 obiecuje usunięcie; musi być wykonalne w 30 dni. | `Legal/regulamin.md` § 14, `Legal/polityka-prywatnosci.md` pkt 9 |
| K3 | **W ROADMAPIE jako US-2.12** (`PLAN-KOMERCJALIZACJA-EPIKI.md`): wymiana Clarity/GA na analitykę bez cookies hostowaną u nas (Plausible/Umami). | Sprzeczność z polityką cookies; pomiar ruchu jest potrzebny. | `Services/AnalyticsOptions.cs`, `wwwroot/js/consent.js` |
| K4 | Linki do regulaminu, polityki i cookies w nagłówku/stopce aplikacji (czat, analiza), nie tylko na landingu. Trasa `/cookies` lub sekcja. | Użytkownik w czacie nie ma jak dotrzeć do dokumentów ani cofnąć zgody na cookies. | `Components/Layout/MainLayout.razor`, `Program.cs:571-587` |
| K5 | Atrybucja CC BY 4.0 (JuDDGES/pl-nsa, EUR-Lex) na stronie „O systemie" i przy źródłach. | Warunek licencji, otwarty od miesięcy. | `OSystemie.razor`, `RUNBOOK-INGESTIA-NSA.md:31` |
| K6 | `Auth:TermsVersion` = identyfikator wersji z opublikowanego regulaminu; mechanizm ponownej akceptacji przy zmianie wersji. | Pkt 12 wymaga informowania o zmianach; zapis zgody z wersją już jest. | `Services/Auth/AuthOptions.cs` |
| K7 | Produkcyjny `Llm:Provider` wyłącznie na endpoint w EOG; usunąć ścieżkę Google AI Studio i OpenRouter z runbooków produkcyjnych. | Bez tego zdanie „treść nie opuszcza EOG" jest fałszywe. Blocker sprzedaży wg planu Tor C. | `RUNBOOK-LLM-PROVIDER.md`, konfiguracja wdrożenia |

---

## 7. Pytania do prawnika (w kolejności wagi)

1. Podmiot: JDG w Polsce czy spółka (i czy naprawdę niemiecka UG)? Od tego zależy prawo właściwe,
   terminologia pkt 9 i VAT/KSeF.
2. Czy ograniczamy usługę do profesjonalistów (B2B-only) i jak to skutecznie zastrzec? Czy dopuszczamy
   aplikantów/studentów (tryb konsumencki, prawo odstąpienia, zakaz części klauzul)?
3. Umowa powierzenia jako załącznik do regulaminu: wzór, lista podwykonawców, tryb zmian.
4. Czy przy docelowym stosie (model i hosting w PL/UE; Stripe, Resend w USA) §7 polityki może
   deklarować „treść pytań i dokumentów nie opuszcza EOG", a transfer poza EOG ograniczyć do danych
   konta i płatności?
5. Podstawa prawna przekazania treści do modelu: wykonanie umowy (lit. b), nie zgoda. Potwierdzenie
   i usunięcie „ekranu zgody" z polityki.
6. Deklaracja przeznaczenia wg AI Act (sekcja 4.4) i zakaz użycia przez organy wymiaru sprawiedliwości.
7. Status prawny treści z EUR-Lex i SAOS (ponowne wykorzystanie, ewentualna ochrona sui generis bazy
   danych), obowiązki atrybucji CC BY.
8. Tryb reklamacyjny, wymagania techniczne, moment zawarcia umowy: brzmienie.
9. Trial: czy chcemy automatycznego przejścia w płatną subskrypcję z kartą podaną z góry, i jakie
   informacje muszą pojawić się przed startem.
10. Zgoda marketingowa: brzmienie zgodne z prawem komunikacji elektronicznej; czy jedna zgoda
    obejmuje e-mail i komunikaty w aplikacji.

---

## 8. Podsumowanie

Dokumenty nadają się jako **baza do przeróbki, nie do publikacji**. Klauzule ochronne (AI,
odpowiedzialność, konta, płatności) są dobre. Do wycięcia: pseudonimizacja, ekran zgody na AI,
Whisper/Claude/Gemini jako lista dostawców, mobile, Word, Sentry, Google/Apple login, publiczne linki,
doktryna, „TUUL". Do dopisania: podmiot z adresem, prawo właściwe, reklamacje, wymagania techniczne,
B2B-only, umowa powierzenia, przeznaczenie wg AI Act, konkretne okresy retencji (6 mies.), odbiorcy
Stripe/Resend/CloudFerro, cel marketingowy i rozliczeniowy, licencja na treści użytkownika. W kodzie:
log z treścią pytań, usuwanie konta, decyzja o analityce, linki do dokumentów w aplikacji, atrybucja
CC BY, produkcyjny model tylko w EOG.

Model „7 dni próbne, potem płatne" nie wymaga zmiany konstrukcji pkt 8; wymaga sześciu doprecyzowań
z sekcji 5, z których pierwsze (karta z góry i automatyczne przejście) jest decyzją produktową.

---

## 9. Uzupełnienie po rozmowie (2026-09-01, wieczór): Gaius Lex jako wzorzec i korekty założeń

### 9.1. Czy budować regulamin na `gaius-lex.pl/regulamin`?

Regulamin Gaius Lex (Flathub sp. z o.o., KRS 0001009006, wersja z 12.11.2025) jest napisany pod polską
ustawę o świadczeniu usług drogą elektroniczną i ma **wszystko, czego brakuje w wersji od znajomego
prawnika**: pełne dane podmiotu, „usługi dla przedsiębiorców" z osobnym trybem dla konsumentów, tryb
reklamacyjny (14 dni), wymagania techniczne, prawo polskie i sąd w Krakowie, faktury elektroniczne w 3 dni
robocze, wsparcie techniczne, procedurę blokady konta z prawem do eksportu danych, przeniesienie praw do
wyników na użytkownika, kasowanie kont nieaktywnych 12 mies., 7-dniowy trial z weryfikacją telefonem,
oraz obowiązek anonimizacji danych osób trzecich albo zawarcia umowy powierzenia.

**Rekomendacja: nie „na podstawie", tylko „według checklisty".** Trzy powody:

1. **Prawa autorskie.** Regulamin to utwór; przepisanie cudzego z podmienioną nazwą to ryzyko roszczenia
   od konkurenta, który ma własnych prawników. Tekst od znajomego mamy z prawem do edycji.
2. **Inny model biznesowy.** Gaius rozlicza się „Lexami" (wewnętrzna jednostka, zamrażanie salda na 90 dni,
   nielimitowany abonament). Połowa ich regulaminu obsługuje mechanikę, której nie mamy i nie chcemy.
3. **Ich polityka prywatności jest słabsza niż nasz stan faktyczny.** Deklarują Google Analytics, Facebook
   Pixel, Microsoft Clarity oraz dostawców AI w USA (RunPod Inc., Gladia SAS) na klauzulach SCC. My
   możemy napisać uczciwie „treść pytań i dokumentów przetwarzana wyłącznie w EOG, bez pikseli
   marketingowych". Kopiując ich tekst, skopiowalibyśmy też słabości, które są naszą przewagą.

**Co wziąć z Gaius jako strukturę** (kolejność sekcji zgodna z art. 8 uśude): definicje → usługodawca
z pełnymi danymi → zakres usług i przeznaczenie → warunki techniczne → zawarcie umowy i konto (B2B,
oświadczenie o działalności zawodowej) → okres próbny → plany, płatności, faktury, odnowienia → zasady
korzystania i zakazy → AI: charakter wyników i odpowiedzialność (tu wkleić pkt 3–5 od znajomego, są
lepsze) → dane osobowe i powierzenie → prawa do treści i wyników → blokada, wypowiedzenie, eksport →
reklamacje → zmiany regulaminu → prawo właściwe → kontakt.

**Co wziąć z Gaius jako pomysł produktowy:** weryfikacja telefonem przy trialu (tani hamulec na
multikonta, których zakaz mamy w pkt 11) oraz obowiązek anonimizacji danych klientów „chyba że zawarto
umowę powierzenia" (jedno zdanie, które porządkuje naszą lukę 4.3).

### 9.2. Korekty założeń z rozmowy

| Temat | Ustalenie | Wniosek dla dokumentów |
|---|---|---|
| **Dostawca modelu** | Nie zdecydowany; pewne jest tylko: siedziba w UE i model o otwartych wagach (Gemma na 99%). Nie chcemy deklarować CloudFerro. | W regulaminie i polityce: „modele o otwartych wagach uruchamiane na infrastrukturze dostawców z siedzibą w UE/EOG; aktualna lista podwykonawców przetwarzania publikowana w [załączniku / na stronie] i zmieniana z powiadomieniem". Nazwa firmy trafia tylko na listę podwykonawców, którą można zmienić bez zmiany regulaminu. Uwaga językowa: Gemma nie jest open source w sensie licencyjnym, pisać „otwarte wagi", nie „open source". |
| **Poczta / hosting** | W kodzie jest Resend. Rozważane serwery Microsoft w regionie Warszawa (Azure Poland Central). | Ten sam mechanizm: polityka mówi „dostawca hostingu i poczty transakcyjnej w UE/EOG lub na podstawie SCC", nazwy na liście podwykonawców. Jeśli Azure Polska zastąpi Resend (Azure Communication Services) i hosting, cała lista podwykonawców treści jest w EOG i argument „dane nie opuszczają EOG" staje się prosty. Do zdecydowania przed publikacją, nie przed napisaniem. |
| **Pomiar ruchu** | Clarity i GA4 można wyłączyć, ale jakiś pomiar ruchu jest potrzebny. | Użyć analityki bez ciasteczek i bez identyfikatorów osobowych, hostowanej u nas (Plausible lub Umami, self-hosted; ewentualnie Matomo w trybie bez cookies). Wtedy polityka cookies „wyłącznie niezbędne" zostaje prawdziwa, banner zgody znika, a pomiar odwiedzin, źródeł ruchu i konwersji na rejestrację jest. **[do potwierdzenia u prawnika]**: czy analityka bez cookies wymaga zgody wg prawa komunikacji elektronicznej; praktyka rynkowa (i dokumentacja Plausible/Umami) mówi, że nie, ale UODO nie wydał jednoznacznego stanowiska. Alternatywa zerowego ryzyka: statystyki z logów serwera/Cloudflare bez skryptu w przeglądarce. |
| **Aplikacja mobilna, wtyczki (poczta, Word)** | Planowane docelowo, nie teraz. | Jedno zdanie w regulaminie: „Usługa jest dostępna przez aplikację internetową. Inne kanały dostępu (aplikacja mobilna, dodatki do programów), o ile zostaną udostępnione, podlegają regulaminowi, a ich szczególne warunki określa serwis." Sekcje 2.5–2.8 polityki o mikrofonie, zdjęciach i lokalnej bazie wracają dopiero z realną aplikacją. |

### 9.3. Zmiana w liście zadań w kodzie

K3 (decyzja o Clarity/GA) zmienia się na: **zastąpić Clarity/GA analityką bez cookies hostowaną u nas**
(Plausible/Umami), usunąć banner i ciasteczko `omniasi-consent`, dodać host analityki do CSP. Mały zakres:
`Services/AnalyticsOptions.cs`, `wwwroot/js/consent.js`, jeden kontener w `infra/compose.yaml`.

---

## 10. Gotowce: projekty dokumentów dla OmniaSI (2026-09-01)

Na prośbę użytkownika powstały trzy projekty dokumentów dopasowane do stanu aplikacji, do przekazania
prawnikowi. Zbudowane na tekście od znajomego prawnika (klauzule AI, odpowiedzialność, konta, plany,
zmiany regulaminu), z wycięciem nieistniejących funkcji (sekcja 3) i z dopisaniem luk (sekcja 4)
w strukturze zgodnej z art. 8 uśude (sekcja 9.1).

**Jedno źródło prawdy.** Pliki leżą w `src/PrawoRAG.Api/Legal/` jako zasoby wbudowane w aplikację
i są renderowane pod `/regulamin`, `/prywatnosc`, `/cookies` (`Services/Legal/LegalPages.cs`).
To, co czyta prawnik, jest dokładnie tym, co zobaczy użytkownik. Kopie dla prawnika:
`OneDrive - Euvic/Desktop/docs/OmniaSI - projekt 2026-09-01/`.

| Plik | Trasa | Co zawiera |
|---|---|---|
| `Legal/regulamin.md` | `/regulamin` | 18 paragrafów: definicje, usługodawca, zakres i przeznaczenie (AI Act), wymagania techniczne, umowa i konto (B2B, oświadczenie o działalności zawodowej), okres próbny (generyczny, oba warianty z sekcji 5), plany i rozliczenia (pula zapytań, okres od dnia zakupu, faktury), zasady korzystania, AI i charakter wyników, treści użytkownika i tajemnica zawodowa (anonimizuj albo umowa powierzenia), własność intelektualna (atrybucja CC BY, prawa do wyników), dostępność, odpowiedzialność (limit 12 mies., prawo polskie), zawieszenie i rozwiązanie (eksport, konta nieaktywne 12 mies.), reklamacje 14 dni, zmiany 14 dni, prawo polskie i sąd siedziby, końcowe |
| `Legal/polityka-prywatnosci.md` | `/prywatnosc` | administrator, zakres danych zgodny z bazą (pkt 2), tabela celów i podstaw (z marketingiem i rozliczeniami), dane klientów użytkownika i powierzenie (pkt 4), AI (otwarte wagi, EOG, bez trenowania), tabela odbiorców z polami na dostawców, transfer poza EOG rozdzielony na treść (EOG) i konto/płatności, tabela retencji z konkretami (6 mies., 60 min, 5 lat), prawa, cookies i pomiar bez cookies, bezpieczeństwo |
| `Legal/polityka-cookies.md` | `/cookies` | lista dwóch ciasteczek technicznych z czasem życia, deklaracja braku analitycznych i marketingowych, pomiar zagregowany, zarządzanie |

**Pola do uzupełnienia** są w nawiasach kwadratowych wielkimi literami, np. `[NAZWA PODMIOTU]`,
`[DOSTAWCA MODELU]`, `[NARZĘDZIE ANALITYCZNE]`. `LegalPages.HasPlaceholders()` wykrywa je, a test
`LegalPagesTests.Placeholder_detection_finds_unfilled_fields` dziś **oczekuje**, że są; po wypełnieniu
odwrócić asercję, żeby test pilnował, by nic nie zostało.

**Świadome założenia w treści, do potwierdzenia u prawnika** (poza listą z sekcji 7):
- polityka cookies i pkt 10 polityki zakładają wariant „analityka bez cookies hostowana u nas"
  (sekcja 9.2); jeśli zostanie Clarity/GA, obie trzeba przepisać;
- § 6 ust. 3 regulaminu opisuje oba warianty triala (z kartą i bez) warunkowo, żeby decyzja produktowa
  nie wymagała zmiany regulaminu;
- § 14 ust. 6: eksport historii USUNIĘTY z treści (decyzja 2026-09-01: nie planujemy); usunięcie konta „przez
  panel Konta lub e-mailem" (użytkownik: zaimplementowane; w tym repo nie widać, patrz K2);
- § 3 ust. 2 wymienia źródła wg stanu korpusu, w tym EUR-Lex i zbiory z CBOSA;
- `Auth:TermsVersion` ustawione na `2026-09-01`, zgodnie z nagłówkiem dokumentów.

**Zmiany w kodzie w tym kroku:** `PrawoRAG.Api.csproj` (EmbeddedResource `Legal\*.md`),
`Services/Legal/LegalPages.cs` (nowy), `Program.cs` (placeholdery zastąpione trasami z `LegalPages.All`,
link „Cookies" w stopce landingu), `appsettings.json` i `AuthOptions.cs` (`TermsVersion`),
`tests/PrawoRAG.Tests/Ui/LegalPagesTests.cs` (nowy, 7 testów).
