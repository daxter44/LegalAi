# Analiza konkurencji: Omnilexia (2026-09-07)

Drugi bezpośredni konkurent po lexedit (`ANALIZA-KONKURENCJA-LEXEDIT.md`). Dokument
strategiczny — bez zmian w kodzie; rozjazdy jako action itemy.

## Uwaga o wiarygodności źródeł (czytać PRZED wnioskami)

Analiza lexedit była **z pierwszej ręki** — ich strona się renderuje po stronie serwera.
Omnilexia to SPA (Vite/React, CloudFront+S3): `curl` zwraca 523 bajty pustego `<div id="root">`,
bundle JS pod adresem z `index.html` już nie istnieje (S3 404 → fallback na index), Googlebot
i czytniki dostają to samo. **Nie widziałem ani strony produktowej, ani cennika, ani centrum
pomocy.**

Co jest udokumentowane i z czego:
- **z pierwszej ręki**: `masterclass.omnilexia.com` (renderuje się normalnie) — pozycjonowanie,
  referencje klientów, prelegenci, dane spółki;
- **cytaty z materiałów Omnilexii**: benefit KIRP (kirp.pl), komunikaty OIRP Warszawa / Poznań /
  Lublin, PSPP, artykuł kancelarii GWLEX — to jest ich własny tekst marketingowy przedrukowany
  przez izby, więc nazwy funkcji są wiarygodne;
- **niezweryfikowane**: ceny (patrz §7) i wszystko, czego nie ma w powyższych.

Czego **nie wiem** i czego nie zgaduję: jakiego LLM-a używają i gdzie stoi (artykuł PSPP wprost
nie podaje), czy mają prawo UE, czy robią OCR skanów, jaka jest struktura pakietów.

## 0. Kolizja marki — jedyna pozycja pilna, reszta to backlog

Konkurent nazywa się **Omnilexia**, jego asystent nazywa się **Omni Asystent**, sprzedaje
radcom prawnym i aplikantom przez KIRP i izby okręgowe. My w fazach RED-1.1/1.2 (`cdb9a20`,
wdrożone) wprowadziliśmy **OmniaSI** do całego UI, `<title>`, favikony, nagłówków AuthPages/
BillingPages i dokumentów prawnych, a 2026-09-02 kupiliśmy domenę (`PLAN-DEPLOY-2026-09-02.md`
§0.1 — nazwa domeny nigdzie w repo nie zapisana; `ANALIZA-DOKUMENTY-PRAWNE-2026-09-01.md` R16
mówi o adresach `@OmniaSI.pl`).

Fakty, bez rekomendacji nazwy — to decyzja właściciela:
- ta sama kategoria (Legal Intelligence / AI dla prawników), ta sama grupa docelowa
  (radcowie prawni), ten sam rdzeń nazwy „Omni";
- oni są pierwsi na rynku z tym skojarzeniem i mają je wzmacniane przez kanał izbowy;
- ryzyko nie jest tylko wizerunkowe — jeśli mają (lub zgłoszą) znak słowny w klasach 42/45,
  „OmniaSI" w tej samej klasie to problem prawny, nie tylko marketingowy;
- **koszt zwlekania rośnie liniowo**: dziś to zmiana stringów w UI + druga domena + poprawka
  w regulaminie i politykach (R16 i tak jest otwarty); po pierwszym płacącym kliencie dochodzą
  faktury, e-maile transakcyjne Resend, materiały izbowe i pozycjonowanie.

Do rozstrzygnięcia przed uruchomieniem publicznym, nie po. Minimalna wersja due diligence:
sprawdzić rejestr znaków UPRP/EUIPO na „OMNI*" w klasach 42 i 45.

## 1. Czym jest Omnilexia

Polska platforma „Legal Intelligence" (Omnilexia P.S.A., Olsztyn, CEO Jędrzej Kardach).
Funkcje pod ich własnymi nazwami:

| Nazwa u nich | Co to robi (z ich opisu) |
|---|---|
| **Omni Asystent** | czat z AI wsparty przepisami i polskim orzecznictwem, z „odnośnikami do rzeczywistych źródeł" |
| **Głęboka analiza prawna** | research przepisów i orzecznictwa pod konkretny problem; wynik = fragmenty dokumentów + linki do źródeł |
| **Baza Organizacji** | przeszukiwanie własnych materiałów kancelarii (wzory pism, umowy) razem z prawem |
| **Kreator Asystentów AI** | opisujesz cel, kreator dopytuje i buduje asystenta pod powtarzalny proces — jawnie „bez prompt engineeringu" |
| **Biblioteka Asystentów AI** | gotowe, stale dokładane asystenty do konkretnych zadań prawniczych *(jedyna pozycja z podsumowania wyszukiwarki, nie z przedruku izbowego — do potwierdzenia)* |
| **Praca grupowa** | zespół w ramach abonamentu, bez dopłat |
| podświetlanie kluczowych fragmentów | referencja GWLEX: „funkcja podświetlania kluczowych fragmentów pomaga szybciej wychwycić sedno sprawy" |
| projektowanie pism | „przygotowywanie roboczych wersji dokumentów" — bez szczegółów |

**Źródła danych**: polskie przepisy + orzecznictwo + to, co reklamują jako wyróżnik —
**materiały ZUS, UODO, UOKiK, KRRiT** („źródła, których brakuje w wielu ogólnych narzędziach AI").
O prawie UE nie mówią nic.

**Bezpieczeństwo**: audyt certyfikacyjny **ISO 27001** przeprowadzony przez DEKRA (certyfikat
w oczekiwaniu), dane w EOG, treści nie są zapisywane ani używane do trenowania, automatyczne
usuwanie danych osobowych, separacja danych między użytkownikami.

**Dojrzałość**: kancelarie jako design partnerzy od bety (GWLEX, PragmatIQ), referencje imienne
z nazwiskami i rolami, webinary z Greenberg Traurig, partnerstwo z PSPP (od 2025-10),
benefit w KIRP i izbach okręgowych. To nie jest MVP — to produkt w fazie sprzedaży.

## 2. Co to znaczy strategicznie

1. **Drugi walidator rynku, ta sama teza.** Dwóch niezależnych graczy sprzedaje research
   z weryfikowalnymi cytatami tym samym odbiorcom. Kierunek z lexedit doc się nie zmienia.
2. **Nasza obietnica suwerenności właśnie się zawęziła.** Wobec lexedit („amerykańskie API +
   anonimizacja jako mitygacja") mieliśmy kontrast strukturalny. Omnilexia odpowiada
   „EOG + brak treningu + usuwanie danych osobowych + ISO 27001" — to jest materialnie mocniejsza
   linia bazowa i przesuwa „dane nie wyjeżdżają" w stronę table stakes. Ostrzejsze, co nam zostaje
   po CloudFerro/Sherlock, to że **sam model** stoi w PL/UE — ale **nie wolno twierdzić, czego oni
   używają**, bo tego nie wiemy (patrz §7, otwarte pytanie nr 1).
3. **Ich prawdziwym ruchem jest dystrybucja, nie funkcja** (§5). Kanał izbowy jest otwarty także
   dla nas i kosztuje mniej niż którakolwiek funkcja z mapy luk.
4. **Sprzedają zdarzeniami, nie funkcjami.** Masterclass „Jak AI pomaga przygotować firmę do
   kontroli PIP?" (materiał niedatowany, stopka ©2025 — możliwe, że to kampania zeszłoroczna;
   wniosek o *schemacie* jest niezależny od daty) (nowelizacja ustawy o PIP, reklasyfikacja B2B→UoP) to sprzedaż przez
   konkretne, datowane ryzyko klienta, z partnerem-kancelarią jako gwarantem merytorycznym
   i z gotowymi asystentami jako odpowiedzią. Nasza świeżość prawa (nowele niewchłonięte,
   vacatio legis) jest dokładnie pod ten schemat — a nigdzie jej tak nie pokazujemy.
5. **Nasz wyróżnik jakościowy pozostaje niezajęty.** Ani lexedit, ani Omnilexia nie sprzedają
   *uczciwej odmowy* — obaj sprzedają „są prawdziwe źródła". My mamy zmierzoną bramkę cytatów
   i abstynencję (86% end-to-end, 100% anty-halucynacja, pomiar z 2026-09-04). Nikt tego nie
   komunikuje, bo nikt tego nie mierzy.

## 3. Mapa luk (stan naszego produktu vs Omnilexia)

Werdykt, nie tylko znaczek: **KOPIOWAĆ** = tanie i wartościowe; **DECYZJA** = konflikt z zasadą
architektoniczną, wymaga świadomego wyboru; **BACKLOG** = po KAZ; **NIE** = świadomie nie.

| Funkcja Omnilexii | U nas | Werdykt / action item |
|---|---|---|
| Czat z cytowanymi źródłami | ✅ core, z bramką cytatów | bez zmian |
| Głęboka analiza (fragmenty + linki) | ✅ panel źródeł, `/dokument`, lokalizator cytatu | bez zmian |
| Źródła miękkiego prawa: **ZUS / UODO / UOKiK / KRRiT** | ❌ | **KOPIOWAĆ** — §4 |
| Podświetlanie kluczowego fragmentu w wyroku | 🔶 mamy anchor cytatu, nie mamy podświetlenia | **KOPIOWAĆ** — najtańsza rzecz na liście, mamy już lokalizator cytatu i `/dokument/{id}` |
| **Baza Organizacji** (materiały kancelarii) | ❌ i sprzeczna z naszą zasadą | **DECYZJA** — §6 |
| **Kreator / Biblioteka Asystentów AI** | ❌ | **DECYZJA** — §6 |
| Praca grupowa / zespół | ❌ (brak encji organizacji) | **BACKLOG** — `PLAN-KOMERCJALIZACJA.md` §100 wycenia B2B jako „istotnie więcej pracy"; po KAZ |
| Projektowanie pism | ❌ | **BACKLOG** — drugi konkurent z rzędu to ma; potwierdza wniosek z lexedit doc, nie zmienia kolejności (po KAZ) |
| Analiza własnego dokumentu (upload) | ✅ PDF + DOCX, cytowania `[D1]`, dokument nie trafia do bazy | bez zmian; OCR skanów nadal backlog |
| Prawo UE (EUR-Lex 2004+) | ✅ 2916 dok. / 412k chunków | **oni tego nie reklamują** — kandydat na wyróżnik, ale najpierw sprawdzić, czy naprawdę nie mają |
| Świeżość: nowele niewchłonięte, vacatio legis | ✅ zmierzone (świeżość 100%) | **eksponować** — §5 pkt 3 |
| Uczciwa odmowa jako obietnica | ✅ zmierzona | **eksponować** — nikt inny tego nie sprzedaje |
| ISO 27001 | ❌ | **DECYZJA** — §6 |
| Aplikacja mobilna | ❌ | **NIE** (jak w lexedit doc — funkcja skalowania, nie walidacji) |

## 4. Jedyna pozycja „kopiować" bez zastrzeżeń: miękkie prawo

ZUS / UODO / UOKiK / KRRiT to najlepszy stosunek wartości do kosztu z całej listy:
- to jest **ingestia**, czyli nasz najmocniejszy mięsień (EUR-Lex T1+T2, runbooki NSA/CBOSA,
  gotowe kontrakty `ISourceConnector` + `IDocumentNormalizer` — dodanie źródła to nowy konektor);
- to jest funkcja, którą konkurent **wymienia z nazwy jako przewagę** nad ogólnymi narzędziami AI;
- mamy już na czym to punktować: pozycja `uodo-60` w golden secie.

Czego NIE wiem i co trzeba zmierzyć przed decyzją (zasada: liczba + założenie + co ją obali):
1. **wolumen i dostępność** — decyzje UODO i UOKiK są publikowane, ale bez porządnego API;
   interpretacje ZUS idą przez BIP; KRRiT to najmniejszy i najmniej pewny kąsek. Spike: pobrać
   po 20 dokumentów z każdego źródła i przepuścić przez „raport jakości" parsowania
   (`PLAN-WDROZENIA.md` Etap 1) — to samo narzędzie, zero kosztu GPU;
2. **ToS / prawo sui generis do bazy** — ta sama analiza co przy SAOS, `PLAN-KOMERCJALIZACJA.md` §7;
3. **koszt embeddingu** przyrostu korpusu — znany z wcześniejszych przebiegów.

Ostrzeżenie z własnych pomiarów: `project_candidatesperpath_r7_tradeoff` — stałe okno 50 slotów
retrievalu rozmywa się przy każdym powiększeniu korpusu, potwierdzone dwukrotnie. Dołożenie
czwartego korpusu **bez podniesienia i zmierzenia `CandidatesPerPath`** powtórzy tę regresję.
To jest warunek, nie przypis.

Kandydat spoza listy konkurenta, o tej samej naturze: **interpretacje podatkowe (SIP/Eureka MF)** —
dla doradców podatkowych to większa wartość niż KRRiT. Do rozważenia w tym samym spike'u.

## 5. Dystrybucja — to jest ich prawdziwy ruch i jest do skopiowania

Co robią, w kolejności rosnącego kosztu dla nas:
1. **Benefit w izbie**: KIRP + OIRP Warszawa/Poznań/Lublin przedrukowują ich ofertę na własnych
   stronach. To nie jest wyłączność — izby ogłaszają benefity wielu dostawców. Otwarty kanał.
2. **Człowiek z izby po ich stronie**: dr Paweł Kowalski, *Pełnomocnik Prezesa KIRP ds. Nowych
   Technologii*, prowadzi z nimi webinar. To wyjaśnia, skąd benefit KIRP.
3. **Kancelaria jako gwarant merytoryczny**: Greenberg Traurig (partner + associate) prowadzi
   część prawną webinaru, oni część narzędziową.
4. **Design partnerzy z nazwiskami**: GWLEX i PragmatIQ testowały od bety i dziś dają referencje
   opisujące konkretne sprawy (spór o znaki towarowe, spory wspólników).
5. **Bezpłatne webinary Masterclass** pod datowane zdarzenie regulacyjne (kontrole PIP od 8 lipca;
   sam materiał niedatowany — patrz §2 pkt 4).

Dla nas, przy dwóch testerach i pilotażu przed startem (`PLAN-STRATEGIA-PILOTAZ.md`): pozycje 4 i 5
są w zasięgu dziś i kosztują czas, nie pieniądze. Pozycja 1 wymaga produktu, który wytrzyma
publiczny ruch — czyli jest za wdrożeniem CloudFerro, nie przed.

## 6. Trzy decyzje właściciela (nie rekomendacje — konsekwencje)

**6.1 Baza Organizacji.** Wprost łamie zasadę nr 1 z `PLAN-ANALIZA-DOKUMENTOW.md`: *„Dokument NIGDY
nie trafia do korpusu ani do bazy"*, uzasadnioną tajemnicą zawodową — dziś życie dokumentu to
pamięć obwodu Blazora, „zero migracji, zero retencji, zero czyszczenia". Trwałe przechowywanie
materiałów kancelarii dokłada: szyfrowanie at-rest, retencję i gwarantowane usunięcie, umowę
powierzenia (RODO), izolację między organizacjami, backupy zawierające cudze akta. To nie jest
funkcja do dołożenia — to zmiana profilu ryzyka całego produktu i argumentu sprzedażowego, którym
się dziś różnimy. Jeśli tak, to jako osobny, świadomie zaprojektowany moduł, nie jako rozszerzenie
analizy dokumentów.

**6.2 Kreator / Biblioteka Asystentów.** Technicznie to najtańszy sposób na „więcej produktu"
(szablony promptów + zapisane parametry + ekran wyboru), a u nich jest nośnikiem sprzedaży
wertykalnej (asystenci „pod PIP"). Ryzyko jest jakościowe, nie inżynieryjne: asystent, który
wychodzi poza ugruntowanie w cytowanych źródłach, przywraca halucynacje tylnymi drzwiami —
czyli kosztuje nas dokładnie ten wyróżnik, który mamy zmierzony. Wersja bezpieczna: gotowe
**scenariusze pytań** działające przez istniejący pipeline (retrieval + bramka cytatów +
abstynencja), bez własnych, niekontrolowanych promptów użytkownika.

**6.3 ISO 27001.** Rząd wielkości dla mikrofirmy w PL: **~15–40 tys. zł w pierwszym roku**
(konsultant + audyt certyfikacyjny), plus audyty nadzoru rocznie — to jest szacunek rynkowy,
nie oferta; przed jakąkolwiek decyzją trzeba wziąć trzy wyceny. **Co obala potrzebę**: brak
klienta, który pyta o certyfikat w procesie zakupowym. **Co ją potwierdza**: pierwszy pilotażowy
klient korporacyjny/in-house odbijający się o dział zakupów. Do czasu takiego sygnału to jest
wydatek bez zmierzonej wartości — a nasza odpowiedź w międzyczasie („infrastruktura PL/UE,
dane nie wychodzą") jest opisywalna bez certyfikatu.

## 7. Otwarte pytania (zapisane jako niewiedza, nie jako fakt)

1. **Jakiego LLM-a używają i gdzie stoi?** Artykuł PSPP nie podaje modelu ani architektury.
   To jest fakt, na którym stoi całe porównanie suwerenności — dopóki go nie znam, nie twierdzę
   nic o ich stosie. Gdzie szukać: ich **regulamin / polityka prywatności / lista podprocesorów** —
   to zwykle jedyne miejsce, gdzie dostawca nazywa hosta modelu.
2. **Ceny.** Jeden snippet wyszukiwarki podaje **119 zł i 249 zł miesięcznie oraz 14 dni testu
   za darmo**; nie widziałem cennika i nie wiem, co zawiera który pakiet. Traktuję to jako
   **liczbę niepotwierdzoną, nie kotwicę cenową** — inaczej niż 250 zł lexedit, które potwierdził
   właściciel. Jeśli się potwierdzi, to *potwierdza* strukturę z lexedit doc (tier wejściowy
   99–149 jako klin, tier z KAZ/pismami celujący w 250+), a nie ją zmienia. Weryfikacja jest
   tania: 14-dniowy trial pokazałby cennik, pakiety i całą funkcjonalność z pierwszej ręki —
   **najtańsza droga weryfikacji, ale nie darmowa i nie moja do podjęcia**: regulaminy konkurencji
   zwykle zakazują zakładania konta w celu analizy konkurencyjnej, a ścieżka izbowa dodatkowo
   wymaga weryfikacji numeru wpisu, czyli firmowana jest Twoją tożsamością zawodową. Kolejność:
   najpierw przeczytać ich regulamin, potem decyzja — i dopiero potem decyzje z §6.
3. **Czy mają prawo UE i OCR skanów?** Nie wspominają. Jeśli nie mają — mamy dwa wyróżniki,
   o których nie mówimy.

## 8. Wnioski dla kolejności prac

1. **Kolizja marki (§0) — rozstrzygnąć przed uruchomieniem publicznym.** Jedyna pozycja, której
   koszt rośnie z każdym tygodniem.
2. **Kolejność wdrożenia bez zmian.** CloudFerro/Sherlock → wdrożenie → pilotaż. Nic w tej analizie
   nie uzasadnia wywracania `PLAN-DEPLOY-2026-09-02.md`; produkt bez działającego wdrożenia nie
   konkuruje niezależnie od listy funkcji.
3. **Weryfikacja z pierwszej ręki (§7 pkt 2)** — zamieniłaby połowę tego dokumentu z domysłów
   w fakty; do decyzji właściciela po lekturze ich regulaminu, bo idzie przez Twoje nazwisko.
4. **Spike miękkiego prawa (§4)** — bez kosztu GPU, na istniejącym narzędziu raportu jakości;
   warunkiem wdrożenia jest podniesienie i zmierzenie `CandidatesPerPath`.
5. **Komunikacja: świeżość + uczciwa odmowa** (§2 pkt 4–5) — jedyne dwie rzeczy, które mamy
   zmierzone, a których żaden z dwóch konkurentów nie sprzedaje. Zero kosztu inżynieryjnego,
   do dopisania na landing i `/o-systemie`.
6. **Nie gonić**: praca grupowa, pisma, mobile — po KAZ, zgodnie z wnioskami z lexedit doc.

## Źródła

- masterclass.omnilexia.com (z pierwszej ręki, 2026-09-07)
- kirp.pl/benefit/oferta-specjalna-omnilexii-dla-radcow-prawnych-i-aplikantow/
- oirpwarszawa.pl, poznan.oirp.pl, oirp.lu — komunikaty o benefficie (ten sam tekst źródłowy)
- pspp.org.pl/2025/10/01/omnilexia-nowym-partnerem-stowarzyszenia/
- gwlex.pl/omnilexia-wspoltworzenie-narzedzia-dla-prawnikow/
- omnilexia.com/pl — **niedostępne do analizy** (SPA bez SSR, bundle 404)
