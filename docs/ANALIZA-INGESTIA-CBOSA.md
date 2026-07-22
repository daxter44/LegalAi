# Analiza: ingestia orzeczeń sądów administracyjnych (CBOSA / NSA+WSA)

Data: 2026-07-22. Zadanie strategiczne — bez zmian w kodzie; rozjazdy jako action itemy.

## Pytanie

Czy i jak zasilić korpus orzeczeniami z CBOSA (orzeczenia.nsa.gov.pl) — luka domenowa: SAOS nie
obejmuje sądownictwa administracyjnego (NSA + 16 WSA), a to domena o dużej wartości dla praktyki
(podatki, budowlanka, dostęp do informacji publicznej…).

## Ustalenia (stan faktyczny; skorygowane po weryfikacji empirycznej 2026-07-22)

1. **CBOSA nie ma API.** KIDP formalnie wnioskowała do Prezesa NSA o udostępnienie API, powołując
   się na ustawę z 11.08.2021 o otwartych danych (API to forma preferowana, a dla danych
   dynamicznych — obowiązkowa) — bez publicznej odpowiedzi.
2. **Strona jest jednak technicznie DOSTĘPNA i trywialna do sparsowania** (weryfikacja własna,
   wbrew wtórnym doniesieniom o CAPTCHA — właściciel potwierdza brak CAPTCHA, my również jej nie
   napotkaliśmy):
   - lista `/cbo/find?p=1…239383` działa bez blokady; raportuje **2 393 829 orzeczeń** (10/stronę);
   - strony `/doc/<ID>` — prosty HTML z metadanymi (sygnatura, data, sąd, skład, symbol sprawy,
     organ, wynik) + **wersja RTF** pod `/doc/<ID>.rtf`;
   - baza aktualna NA BIEŻĄCO (trafione orzeczenie z 2026-07-21); uwaga: świeże/nieprawomocne
     bywają samą sentencją — uzasadnienia dochodzą później (delta musi umieć AKTUALIZOWAĆ dokument);
   - `robots.txt` zabrania botom `/cbo/find|search|do/*` (strony `/doc/…` NIE są zabronione),
     sitemapa martwa od 2009. Kwalifikacja robots.txt przy własnym crawlu → pytanie do bramki
     prawnej, nie do inżynierii.
   Odrzucone pozostaje tylko OBCHODZENIE blokad (proxy-rotacja jak `ad-m/cbosa`) — jeśli serwer
   zacznie limitować, odpowiedzią jest wolniejsze tempo/wniosek do NSA, nie ukrywanie się.
3. **KLUCZOWE: istnieje gotowy, legalnie opublikowany dataset** —
   **`JuDDGES/pl-nsa`** (Hugging Face, projekt naukowy JuDDGES):
   - ~**1,82 mln orzeczeń** NSA+WSA z pełnymi tekstami (`full_text`, uzasadnienia, tezy),
     bogate metadane (sygnatura, sąd, sędziowie, data, symbol sprawy, `extracted_legal_bases`,
     hasła tematyczne), Parquet, **31 GB**, stan do **2025-03-05**;
   - licencja datasetu: **CC BY 4.0** (atrybucja); same orzeczenia to materiały urzędowe
     (art. 4 pr. aut. — poza ochroną; ta sama kwalifikacja co przy SAOS — do potwierdzenia
     w trwającej bramce prawnej);
   - wariant **`pl-nsa-enriched`**: 2,25 mln wierszy, 54 GB, aktualizowany **2026-01-14**,
     + pola wyekstrahowane Gemini 2.5 Pro (stan faktyczny/prawny). Uwaga spójnościowa:
     wzbogacenia pochodzą z modelu US — jako metadane pomocnicze nie łamią obietnicy
     suwerenności (nie przetwarzają danych użytkownika), ale do promptów bierzemy TYLKO
     oryginalne teksty orzeczeń; własne wzbogacenia (jak QU/AKT) liczymy sami.

## Wnioski architektoniczne (fit do naszego pipeline'u)

- **Backfill = dataset, nie crawl.** Dwufazowa ingestia (fetch|process) pasuje idealnie:
  „fetch" to jednorazowe pobranie parquetów do magazynu surowych; „process" = nowy
  `ISourceConnector`/normalizer (`NsaConnector`) czytający parquet → `DocumentEntity`
  (DocType=Judgment, Source="NSA") → chunking → embedding. Zero sieci w procesie, pełna
  idempotencja jak dziś. Czytanie parquet w .NET: Parquet.Net albo jednorazowa konwersja
  do JSONL skryptem w `tools/`.
- **Skala to główny problem, nie dostęp.** 1,8–2,25 mln orzeczeń ≈ rząd(y) wielkości więcej
  chunków niż obecny korpus (pełny SAOS+ISAP). Szacunek zgrubny: przy śr. kilkunastu tys.
  znaków tekstu → ~15–25 mln chunków → ~30–50 GB samych wektorów (halfvec 1024) + indeks HNSW.
  Wniosek: **NIE ingestować całości na start** — potrzebna strategia podzbioru.
- Wąskim gardłem będzie (jak przy SAOS/ISAP) CPU-bound preprocessing, nie GPU — planować
  równoległość przetwarzania wstępnego.

## Wymaganie biznesowe (właściciel, 2026-07-22)

**Produkt wymaga PEŁNEJ bazy sądownictwa administracyjnego** — podzbiór może być tylko etapem
przejściowym, nie stanem docelowym. Warianty niżej czytać jako KOLEJNOŚĆ dochodzenia do całości,
nie jako wybór zakresu.

## Droga do pełnej bazy — dwie ścieżki (mogą iść RAZEM)

| | Backfill z datasetu JuDDGES + własny crawl delty | Pełny własny crawl CBOSA |
|---|---|---|
| Objętość do pobrania | ~0,14–0,57 mln stron (delta 2025-03→dziś + braki) | ~2,39 mln stron (+ listy) |
| Czas przy uczciwym tempie 1–2 req/s | dni | ~14–28 dni ciągiem |
| Obciążenie serwera sądu | minimalne | istotne |
| Zależność od strony trzeciej | JuDDGES (CC BY 4.0, atrybucja) | brak |
| Kontrola jakości/formatu | format JuDDGES + własny parser delty | jeden własny format od początku |

Rekomendacja techniczna: **hybryda** — backfill z `pl-nsa` (od razu 1,8–2,25 mln pełnych tekstów,
zero obciążania CBOSA), własny crawler dociąga deltę i uzupełnia braki (rekonsyliacja po
`judgment_id`/sygnaturze), a docelowo staje się stałym mechanizmem świeżości. Pełny własny crawl
zostaje jako plan B, gdyby prawnik zakwestionował dataset (albo właściciel wolał niezależność —
technicznie wykonalny, tylko wolniejszy i mniej grzeczny wobec infrastruktury sądu).

## Warianty kolejności ingestii (przy hybrydzie; do decyzji tylko KOLEJNOŚĆ)

Docelowo wchodzi CAŁOŚĆ (wymaganie biznesowe); kolejność embedowania można sterować wartością:
NSA/wyroki najpierw → domeny pilotażowe → reszta WSA. Rozkład liczności per sąd/typ/rok da się
policzyć Z SAMYCH METADANYCH datasetu (tani krok #1). Skala całości bez zmian: ~15–25 mln chunków,
30–50 GB wektorów + HNSW — do zwymiarowania dysku/RAM na 3060 PRZED startem procesu.

## Świeżość (delta-sync)

Własny crawler delty (uczciwe tempo, jawny User-Agent, bez obchodzenia blokad): enumeracja po
zakresie dat w wyszukiwarce → nowe/zmienione `/doc/<ID>` (+RTF). Musi umieć AKTUALIZOWAĆ dokument
(sentencja → później uzasadnienie; nieprawomocne → prawomocne) — pipeline już to wspiera
(ContentHash + re-process). Równolegle formalny **wniosek o ponowne wykorzystywanie ISP / API do
NSA** (jak KIDP) — legitymizuje dostęp długoterminowo.

## Action itemy

- [ ] **Prawne (do bramki 0.5, zewnętrzny prawnik — dopisać do zleconego zakresu, NIE robić
      researchu samodzielnie):** (a) dataset JuDDGES (CC BY 4.0 — forma atrybucji w produkcie)
      i status orzeczeń (art. 4 pr. aut.); (b) regulamin/nota CBOSA + kwalifikacja robots.txt
      przy własnym crawlu; (c) wniosek ISP/API do NSA.
- [ ] **Tani krok #1:** pobrać SAME METADANE `pl-nsa` i policzyć rozkład per sąd/rok/typ/symbol
      → kolejność ingestii + wymiarowanie dysku; przy okazji rekonsyliacja liczności z 2 393 829
      ze strony (czego brakuje w datasecie).
- [ ] **Decyzja właściciela:** hybryda vs pełny własny crawl; raw vs enriched (rekomendacja: raw).
- [ ] Po decyzjach: plan implementacji (`NsaConnector` + crawler delty) — osobny PLAN-*.md.

## Rekomendacja (zrewidowana po weryfikacji empirycznej)

Cel = PEŁNA baza (2,39 mln). Droga: **hybryda** — backfill z `JuDDGES/pl-nsa` (szybko, legalnie,
bez obciążania serwera sądu) + własny, jawny crawler CBOSA na deltę/braki, docelowo stały
mechanizm świeżości; równolegle wniosek do NSA o API/ISP. Pełny własny crawl = plan B (wykonalny:
~2,39 mln stron, 14–28 dni przy 1–2 req/s). Bez obchodzenia ewentualnych blokad — gdy serwer
ograniczy, zwalniamy albo eskalujemy wnioskiem, nie proxy.

## Źródła

- KIDP — wniosek o API do CBOSA: https://kidp.pl/kidp-z-wnioskiem-do-nsa-o-latwiejszy-dostep-do-bazy-orzeczen-sadow-administracyjnych
- JuDDGES/pl-nsa: https://huggingface.co/datasets/JuDDGES/pl-nsa
- JuDDGES/pl-nsa-enriched: https://huggingface.co/datasets/JuDDGES/pl-nsa-enriched
- Projekt JuDDGES: https://juddges-project.eu/outputs/
- robots.txt CBOSA: https://orzeczenia.nsa.gov.pl/robots.txt
- Scraper obchodzący blokady (ODRZUCONY): https://github.com/ad-m/cbosa
