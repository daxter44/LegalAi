# PLAN: Strategia testów, deployu i marketingu (pilotaż)

Data: 2026-07-08 (wersja 3). Ustalenia wiążące:
- **wyłącznie Bielik, zero amerykańskich LLM API**;
- **bez zakupu sprzętu** (MacBook M4 zostaje narzędziem deweloperskim, nie serwerem docelowym);
- **pełny korpus embeddowany RAZ, zdalnie, z datą migawki** → test lokalny → wypchnięcie na serwer
  produkcyjny, gdy zostanie wynajęty; potem tylko codzienne delty.

Kontekst: projekt finansowany z własnego budżetu. Zasób zewnętrzny: shiftlaw.pl — 590 zarejestrowanych
prawników (kanał rekrutacji testerów i przyszły kanał sprzedaży).

---

## 0. Naczelne zasady

1. **Full Bielik = koszt zmienny ≈ 0.** Płacimy za pojemność (wynajęty akcelerator), nie za użycie.
   CostGuard chroni pojemność GPU przed przeciążeniem/nadużyciem, nie portfel — limity zostają.
2. **Embedding to jednorazowa robota wsadowa, nie cecha produkcji.** To, że MacBook liczy embeddingi
   bardzo długo, niczego nie przesądza o serwowaniu: wynajęte GPU na godziny robi pełny korpus za
   kilkadziesiąt zł, a codzienne delty (dziesiątki dokumentów) obsłuży TEI na CPU na serwerze w minuty.
   Ciężki jest tylko start — i robimy go raz.
3. **Pełny korpus zamiast korpusu dziedzinowego.** Filtrowanie po dziedzinie prawa nie istnieje w API SAOS
   ani w naszym konektorze (tylko typ sądu + daty — `SaosOptions.cs`); przybliżenie po literze repertorium
   w sygnaturze (C=cywilne, K=karne, P=pracy, U=ubezpieczeń, G=gospodarcze) to nowy kod i ryzyko błędnej
   klasyfikacji. Skoro barierą kosztową embeddingu przestało być GPU, pełny korpus jest prostszy, lepszy
   jakościowo (mniej odmów z powodu dziur) i upraszcza rekrutację testerów (dowolna specjalizacja).
4. **Suwerenność danych to wyróżnik**: w 100% polski stos (Bielik + mmlw + polski reranker), dane nie
   opuszczają naszej infrastruktury — argument, którego nie ma nikt na GPT/Claude.

---

## 1. Stan faktyczny (audyt repo, 2026-07-08)

Co JEST: bramka dostępu (kody + CostGuard/RateGuard), ścieżka Bielika sprawdzona end-to-end na M4,
ingestia dwufazowa (`fetch`/`process`/`discover`/`sync-eli` — pełna ingestia to konfiguracja, nie nowy kod),
TEI+Postgres/pgvector w `infra/compose.yaml`, harness E5 (za darmo w trybie `--chat` na Bieliku),
151 testów zielonych, retencja 6 mies.

Czego NIE MA: Dockerfile api/workera, appsettings.Production, HTTPS, CI/CD, backupy, monitoring,
scheduler delta-sync (założony cron), polityka prywatności/regulamin testów.

**Rozjazdy kod ↔ decyzje (do uporządkowania po stronie zespołu, poza tym planem):**
- `"Provider": "claude"` w `src/PrawoRAG.Api/appsettings.json:19` i `src/PrawoRAG.Eval/appsettings.json:20`,
  fallback `claude` w `LlmServiceCollectionExtensions.cs:16`, rekomendacja Claude w `PLAN-WDROZENIA.md` Etap 6
  → przestawić na `local`; docelowo rozważyć usunięcie ścieżki Claude (audytowalna obietnica suwerenności, §5).
- Zakres SAOS w `src/PrawoRAG.Ingestion/appsettings.json`: `COMMON`+`APPEAL` od 2023 → do poszerzenia na
  pełny korpus (§3.2); `MaxItems=3` (dev) → zdjąć przy pełnym fetchu.

---

## 2. Strategia testów

### T0 — bramka jakości (przed jakimkolwiek testerem)
1. **E5 przed każdym deployem, w pełnym zakresie** (retrieval + `--chat` — na Bieliku za darmo).
   Progi: brak regresji vs ostatni zapis (dziś ~86% recall / 100% anty-halucynacja).
   Wyniki do `docs/EVAL-LOG.md` (data, commit, metryki) — „CI za darmo".
2. Golden set żyje: każdy zasadny błąd od testera → nowy przypadek regresyjny.
3. **Punkt uwagi — jakość językowa Bielika 11B**: wartość produktu stoi na retrievalu, cytatach i uczciwej
   odmowie, nie na elokwencji. W feedbacku rozdzielać „błąd merytoryczny" od „odpowiedź toporna językowo"
   (drugie = tuning promptu, nie zmiana architektury).

### T1 — alfa (po zbudowaniu pełnego korpusu): 2–3 zaprzyjaźnionych prawników
- 30-min sesja na żywo (obserwujemy, nie pomagamy) + tydzień swobodnego używania.
- Może działać na tymczasowej infrastrukturze (MacBook + Cloudflare Tunnel) — inferencja czatu na M4
  działa (runbooki); to embedding był wolny, a ten jest już za nami.
- Kryterium wyjścia: 0 blokerów, testerzy wracają sami.

### T2 — zamknięta beta (na wynajętym serwerze): 20–30 prawników z bazy shiftlaw, kohortami po ~10
- Dzięki pełnemu korpusowi **bez ograniczenia specjalizacji** — ankieta zgłoszeniowa nadal pyta
  o dziedzinę (do analizy odmów per dziedzina), ale nie odsiewa.
- Kohorty po ~10 co 2 tygodnie: chodzi o pojemność inferencji (kolejkowanie) i czas na iterację.
- Onboarding: strona „O systemie" + 3 przykładowe „dobrze zadane" pytania + jawne ograniczenia
  (rejestr potoczny, zmiana tematu = nowa rozmowa) + **data migawki korpusu widoczna w UI**
  („stan prawny źródeł na: DD.MM.RRRR", aktualizowana przez delta-sync) — buduje zaufanie i uczciwie
  komunikuje świeżość.

### Metryki bety (dashboard = zapytanie SQL raz w tygodniu)
| Metryka | Sygnał | Cel orientacyjny |
|---|---|---|
| Aktywacja: ≥5 pytań w 1. tygodniu | czy spróbowali | ≥70% zaproszonych |
| Powrót w 2. tygodniu bez przypomnienia | czy jest realna wartość | ≥40% |
| 👍/👎 | jakość odpowiedzi | ≥75% pozytywnych |
| % odmów, także per dziedzina testera | za wysoki = dziury/retrieval; podejrzanie niski = ryzyko halucynacji | 10–25% |
| Czas do pierwszego tokena i tok./s (log serwera) | czy inferencja nadąża | TTFT < 3 s |
| Kliknięcia w źródła [n] (dołożyć logowanie) | czy cytaty budują zaufanie | rosnące |
| Po 4 tyg.: „czy użyłbyś w realnej sprawie klienta?" | gotowość do płacenia | jakościowe |
- Co tydzień: przegląd 👎 + transkryptów odmów → klasyfikacja (retrieval / generacja / UX / luka danych)
  → golden set. 5–8 wywiadów 1:1 o workflow („pokaż, jak robiłeś research w ostatniej sprawie").

---

## 3. Strategia deployu

### 3.1 Docelowa architektura produkcyjna — wynajęty serwer z GPU (bez zakupu sprzętu)
```
[wynajęty serwer GPU, np. Hetzner GEX44: RTX 4000 SFF Ada 20 GB VRAM / 64 GB RAM — cennik ~€184/mies., do potwierdzenia]
  Caddy (automatyczny HTTPS)
   └── PrawoRAG.Api (Blazor Server + /api/chat)
  Postgres + pgvector (pełny korpus ~4,9 mln wektorów; wolumen przywieziony z sesji embeddingu — §3.2)
  TEI CPU (embedding zapytań i dziennych delt)   Ollama + Bielik 11B Q5 (GPU)
  cron: sync-eli + fetch SAOS (delta od daty migawki), pg_dump co noc → tanie object storage
```
- Zero wywołań na zewnątrz w ścieżce odpowiedzi (suwerenność, §5).
- Wynajem zamiast zakupu = zero ryzyka nietrafionej inwestycji: za mały → wypowiadamy i bierzemy większy,
  wolumen Postgresa i kontenery przenoszą się w godziny.
- 64 GB RAM ma zapas na HNSW pełnego korpusu (~25–30 GB); 20 GB VRAM zmieści Bielika (~8 GB) + docelowo
  TEI/reranker na GPU, gdy pełny korpus uzasadni ponowny test rerankera.
- Serwer wynajmujemy dopiero, gdy korpus jest gotowy i przetestowany (§3.2) — do tego czasu licznik
  miesięczny nie bije. Alfę można przeprowadzić jeszcze na MacBooku + Cloudflare Tunnel (0 zł).

### 3.2 Korpus: „raz a dobrze" — potok zdalnego embeddingu z datą migawki

**Decyzja nieodwracalna do potwierdzenia przed startem**: model embeddingów
(`sdadas/mmlw-retrieval-roberta-large-v2`) jest zablokowany na życie korpusu — jego zmiana = re-embedding
całości. Świadomie zostajemy przy nim (najlepszy dostępny polski model retrievalowy, już zwalidowany).

**K0 — pilot tempa fetch (1–2 dni, od zaraz):** pobrać wąski zakres dat (np. 1 miesiąc orzeczeń) i zmierzyć
realne tempo API SAOS → ekstrapolacja czasu pełnego fetchu (to jedyna niewiadoma kalendarzowa planu).
**K1 — pełny fetch (tło, od zaraz po K0):** MacBook albo najtańszy VPS; to tylko wywołania API (lekkie,
bez GPU). Konfiguracja do poszerzenia: `CourtType` przebiegami per typ sądu (COMMON+APPEAL, SUPREME, …),
`JudgmentDateFrom` wstecz, `MaxItems` zdjęte, ELI `Discover:Enabled=true` (1980→dziś). Fetch jest
dwufazowy i idempotentny — przerwania niegroźne. Surowe dane: rzędu kilkunastu–kilkudziesięciu GB.
**K2 — sesja GPU (wynajem godzinowy: RunPod / Vast.ai / Lambda, RTX 4090 ~0,4–0,7 USD/h):** upload surowych
danych, compose (pgvector + TEI GPU), `Mode=process` na pełnym korpusie, budowa indeksu HNSW (wysokie
`maintenance_work_mem`, rdzenie CPU boxa), zapis **daty migawki** (judgmentDateTo / data fetchu ELI) do
konfiguracji/metadanych. Szacunek: kilka–kilkanaście godzin ściany zegara, **koszt rzędu 50–150 zł** —
budżetować z zapasem, to i tak jednorazowe grosze.
**K3 — walidacja ZANIM wyłączymy box:** E5 retrieval na pełnym korpusie (bramka z `PLAN-WDROZENIA.md`
Etap 4) + od razu ponowny test rerankera (druga instancja TEI na tym samym GPU — dokładnie ten moment
„pełny korpus, więcej szumu", na który test czekał). Poprawki → dogrywka na tym samym boxie, bez
ponownego wynajmu.
**K4 — artefakty do object storage (B2/R2, grosze):**
- `pg_dump` (format custom) — przenośny wszędzie, ale restore odbudowuje HNSW od zera (godziny);
- tar wolumenu Postgresa (przy przypiętym obrazie `pgvector/pgvector:pg17`) — restore w minuty
  **na Linux x86_64** (produkcja); nie przenosić wolumenu binarnie między architekturami (Mac ARM).
Pobrać oba. To jest nasze „raz a dobrze": każdy kolejny serwer dostaje gotowy korpus z datą migawki.
**K5 — test lokalny (MacBook):** restore z `pg_dump` (budowa indeksu przez noc — jednorazowo), pełny RAG
lokalnie wg runbooków M4. Uwaga: przy pełnym korpusie na MacBooku zapytania będą wolniejsze (indeks
większy niż RAM) — to test funkcjonalny, nie wydajnościowy.
**K6 — produkcja:** wynajem serwera (§3.1), restore tar-a wolumenu, cron delta-sync **od daty migawki**
(ELI + SAOS codziennie; dzienne przyrosty embedduje TEI CPU w minuty), data migawki w UI aktualizowana.

### 3.3 Kroki wdrożeniowe poza korpusem
1. **D0 — konteneryzacja i konfiguracja produkcyjna**: Dockerfile Api + worker, odkomentowanie usług
   w `infra/compose.yaml`, `appsettings.Production.json` (`Llm:Provider=local`!), sekrety przez env.
2. **D1 — ekspozycja**: domena; na okres alfy Cloudflare Tunnel (MacBook), na produkcji Caddy/Let's Encrypt.
   `Access:Enabled=true` od pierwszego dnia publicznej dostępności.
3. **D2 — operacje na serwerze**: cron delta-sync, pg_dump co noc do object storage, UptimeRobot (darmowy),
   log dziennego użycia (licznik CostGuard zrzucany przed północą UTC), `OLLAMA_NUM_PARALLEL=2`
   + logowanie TTFT/tok./s (metryka §2 — dane do ewentualnej decyzji o większym serwerze).

Świadome skróty pilotażu: CostGuard in-memory (restart zeruje dzień — bez skutków finansowych przy własnej
inferencji), brak CI (bramka E5 ręczna, logowana), brak panelu admina do kodów (env wystarcza dla ≤30 osób).

---

## 4. Koszty

| Pozycja | Kiedy | Kwota |
|---|---|---|
| K0–K1 fetch | teraz, tło | 0 zł (MacBook) lub ~20–30 zł/mies. (najtańszy VPS) |
| K2–K4 sesja GPU + transfer + storage artefaktów | raz | ~50–200 zł (budżetować górną granicę) |
| Alfa: MacBook + Cloudflare Tunnel + domena | 2–3 tyg. | ~50 zł/rok (domena) |
| Beta/produkcja: serwer GPU (np. GEX44) | od kohorty 1 | ~800–850 zł/mies. |
| Backupy (B2/R2) + monitoring | stale | ~15 zł/mies. |
| Koszt zmienny za zapytanie | — | ~0 zł |

Sekwencja chroni budżet: miesięczny koszt ~800 zł startuje dopiero, gdy korpus jest zbudowany,
zwalidowany (E5 na pełnej skali) i alfa potwierdziła brak blokerów — płacimy za serwowanie wartości,
nie za budowę. Dźwignia awaryjna, gdyby 800 zł/mies. było za dużo: niższa kwantyzacja (Q4) lub słabszy
GPU — wyłącznie po pomiarze E5 `--chat` bez regresji, nigdy „na oko".

---

## 5. Zaufanie i zgodność (dla prawników to kryterium zakupu)

1. **Suwerenność danych — najmocniejszy argument**: pytania, rozmowy i źródła nie opuszczają naszej
   infrastruktury; w 100% polski stos. Po usunięciu ścieżki Claude z kodu (§1) obietnica audytowalna
   w źródłach. Dla kancelarii o podwyższonych wymaganiach: opcja on-premise (ta sama konfiguracja `Llm:Local`).
2. Mimo lokalnej inferencji — w UI zalecenie „opisuj problem abstrakcyjnie, bez danych osobowych klientów"
   (logi i backupy też są danymi; edukacja użytkownika).
3. Przed pierwszym zewnętrznym testerem: polityka prywatności + krótki regulamin testów (charakter
   „wstępnego researchu do weryfikacji" — baner już jest; retencja 6 mies. — zaimplementowana; zgoda na
   kontakt w sprawie feedbacku).
4. Mailing do bazy shiftlaw — zweryfikować zgody marketingowe (inny produkt). Kanał bezpieczny niezależnie
   od zgód: baner wewnątrz aplikacji shiftlaw.

---

## 6. Strategia marketingu

Sekwencja: **najpierw dowody, potem zasięg**.

### M1 — teraz (równolegle z K0–K2): cicha rekrutacja
- Pozycjonowanie — dwa filary, wszędzie te same:
  1. **„Research prawny z prawdziwymi, klikalnymi cytatami. Gdy nie ma źródła — odmawia, zamiast zmyślać."**
  2. **„W 100% polski stos AI. Twoje pytania nigdy nie trafiają do amerykańskich chmur."**
  Wspierająco: mechanizm nowelizacji („obowiązuje od…") i jawna **data stanu prawnego źródeł** w UI.
- Landing z listą oczekujących (może być rozszerzenie strony „O systemie") — e-mail + specjalizacja.
- Kanał shiftlaw: baner w aplikacji + (po weryfikacji zgód) jeden e-mail do 590 prawników: „ograniczona
  beta, X miejsc, za darmo, w zamian za feedback" — limit miejsc jest prawdziwy (pojemność inferencji).
- **Społeczność Bielika**: zgłosić PrawoRAG do SpeakLeash jako wdrożenie/case study — darmowy zasięg
  i uwiarygodnienie technologiczne.

### M2 — w trakcie bety: budowanie dowodów
- Testimoniale i mini-case-studies za zgodą testerów (najlepszy materiał: „pokazał nowelizację, której
  nie było jeszcze w tekście jednolitym").
- Build-in-public po polsku na LinkedIn (1 post/tydz.): konkrety z budowy — znalezione błędy jakości danych
  („tekst sprzed reformy 2023: 15 lat zamiast 30") pokazują rygor lepiej niż obietnice.
- Zero płatnej reklamy.

### M3 — po becie (decyzja ~tydzień 10–12): otwarcie
- Warunek: metryki §2 + ≥3 testimoniale + zmierzona pojemność obecnego serwera.
- Pierwszeństwo dla waitlisty, potem baza shiftlaw, program poleceń od beta-testerów.
- Cena: badana w wywiadach 1:1. Kotwica: Lex/Legalis w setkach zł/mies.; nasza struktura kosztów
  (inferencja ≈ 0 zł/zapytanie) pozwala na cenę, przy której konkurencja na API frontierowych nie ma marży.
- Rozważyć wiązanie z shiftlaw po walidacji wartości.

---

## 7. Harmonogram orientacyjny (kalendarz dyktuje fetch SAOS — zmierzyć w K0!)

| Tydzień | Korpus/deploy | Testy | Marketing |
|---|---|---|---|
| 1 | K0 pilot tempa fetch → urealnienie tego harmonogramu; start K1 (tło); D0 konteneryzacja | — | landing + waitlista; weryfikacja zgód shiftlaw |
| 2–4 (zależnie od tempa API) | K1 fetch trwa; przygotowanie compose na sesję GPU | — | baner w shiftlaw; zgłoszenie do SpeakLeash |
| +1 | K2 sesja GPU → K3 walidacja E5 + test rerankera → K4 artefakty | — | — |
| +2 | K5 test lokalny; D1 tunel na MacBooku | T1 alfa (2–3 prawników) | — |
| +3 | K6: wynajem serwera GPU, restore, cron, D2 operacje | poprawki z alfy | e-mail rekrutacyjny |
| +4–7 | pomiar TTFT/kolejek | T2 kohorty 1–2; wywiady 1:1 | build-in-public |
| +8–9 | — | kohorta 3; podsumowanie metryk | testimoniale |
| +10 | decyzja: większy serwer? | raport z bety | decyzja o otwarciu i cenie |

## 8. Otwarte decyzje (do potwierdzenia)
1. Zakres pełnego korpusu na start: typy sądów (COMMON+APPEAL? +SUPREME/TK/KIO?) i `JudgmentDateFrom`
   (jak głęboko wstecz) — wpływa na czas fetchu i rozmiar, nie na architekturę.
2. Potwierdzenie modelu embeddingów przed K2 (decyzja nieodwracalna na życie korpusu).
3. Dostawca sesji GPU (RunPod / Vast.ai / Lambda) i serwera produkcyjnego (Hetzner GEX44 / OVH / inny —
   sprawdzić aktualne cenniki i dostępność).
4. Zgody shiftlaw: e-mail dozwolony czy tylko baner in-app?
5. Porządki po decyzji full-Bielik (po stronie zespołu, poza tym planem): defaulty `Provider`,
  `PLAN-WDROZENIA.md` Etap 6, ewentualne usunięcie ścieżki Claude; zakres SAOS w appsettings ingestii.
