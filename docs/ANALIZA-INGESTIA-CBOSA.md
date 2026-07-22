# Analiza: ingestia orzeczeń sądów administracyjnych (CBOSA / NSA+WSA)

Data: 2026-07-22. Zadanie strategiczne — bez zmian w kodzie; rozjazdy jako action itemy.

## Pytanie

Czy i jak zasilić korpus orzeczeniami z CBOSA (orzeczenia.nsa.gov.pl) — luka domenowa: SAOS nie
obejmuje sądownictwa administracyjnego (NSA + 16 WSA), a to domena o dużej wartości dla praktyki
(podatki, budowlanka, dostęp do informacji publicznej…).

## Ustalenia (stan faktyczny)

1. **CBOSA nie ma API.** KIDP formalnie wnioskowała do Prezesa NSA o udostępnienie API, powołując
   się na ustawę z 11.08.2021 o otwartych danych (API to forma preferowana, a dla danych
   dynamicznych — obowiązkowa) — bez publicznej odpowiedzi.
2. **Scraping wyszukiwarki jest aktywnie blokowany**: CAPTCHA po kilku zapytaniach;
   `robots.txt` zabrania `/cbo/search`, `/cbo/find`, `/cbo/do/*`, wariantów `*.txt` i w całości
   blokuje GPTBot/MSNbot. Zadeklarowana `sitemap.xml` jest MARTWA (pod-sitemapy z 2009 r.).
   Istniejące scrapery (np. `ad-m/cbosa`) obchodzą blokady przez proxy — **droga odrzucona**:
   dla komercyjnego produktu ryzyko prawne/wizerunkowe nie do przyjęcia.
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

## Warianty podzbioru na start (do decyzji)

| Wariant | Zakres | Szacunek | Za | Przeciw |
|---|---|---|---|---|
| A | tylko NSA (bez WSA), wyroki, 2015+ | ~200–400 tys.? (do zmierzenia z metadanych) | najwyższa ranga orzeczeń, precedensotwórcze | traci świeże linie WSA |
| B | domenowy (symbole spraw, np. podatki 6110*…) | zależnie od domeny | pod pilotaż z konkretną grupą prawników | wymaga wyboru domeny docelowej |
| C | całość | 1,8–2,25 mln | kompletność | koszt dysku/czasu; przetestować dopiero po A/B |

Rozkład liczności per sąd/typ/rok da się policzyć Z SAMYCH METADANYCH datasetu przed decyzją
(tani krok #1).

## Świeżość (delta-sync) — otwarte

Dataset kończy się na 2025-03 (raw) / aktualizacja 2026-01 (enriched). Opcje:
1. konsumować kolejne aktualizacje datasetu JuDDGES (zero ryzyka, opóźnienie miesiące),
2. **wniosek o ponowne wykorzystywanie ISP / API do NSA** (jak KIDP) — czysto prawna ścieżka,
   długa, ale docelowo właściwa dla produktu komercyjnego,
3. ostrożny crawl pojedynczych `/doc/…` (robots ich nie zabrania) TYLKO po pozytywnej opinii
   prawnej — nie projektować przed nią.
Dla orzecznictwa świeżość jest mniej krytyczna niż dla aktów (AKT pilnuje prawa) — opcja 1
wystarcza na start.

## Action itemy

- [ ] **Prawne (do bramki 0.5, zewnętrzny prawnik — dopisać do zleconego zakresu, NIE robić
      researchu samodzielnie):** (a) status prawny korzystania z datasetu JuDDGES (CC BY 4.0 —
      forma atrybucji w produkcie) i samych orzeczeń (art. 4 pr. aut.); (b) regulamin/nota CBOSA;
      (c) ścieżka wniosku o ponowne wykorzystywanie ISP do NSA.
- [ ] **Rozpoznanie taniego kroku:** pobrać SAME METADANE `pl-nsa` (bez tekstów) i policzyć
      rozkład per sąd/rok/typ/symbol → dane do decyzji o podzbiorze (wariant A/B/C).
- [ ] **Decyzja właściciela:** podzbiór na start + czy raw czy enriched (rekomendacja: **raw**,
      własne wzbogacenia policzymy jak dla SAOS).
- [ ] Po decyzjach: plan implementacji `NsaConnector` (osobny dokument, wzorzec PLAN-*.md).

## Rekomendacja

Ingestia z CBOSA przez scraping — **nie**. Ingestia przez dataset **JuDDGES/pl-nsa** — **tak**,
zaczynając od analizy metadanych i podzbioru (wariant A lub B), z atrybucją CC BY i po
potwierdzeniu w bramce prawnej. Delta-sync: aktualizacje datasetu teraz, równolegle formalny
wniosek do NSA o API/ISP.

## Źródła

- KIDP — wniosek o API do CBOSA: https://kidp.pl/kidp-z-wnioskiem-do-nsa-o-latwiejszy-dostep-do-bazy-orzeczen-sadow-administracyjnych
- JuDDGES/pl-nsa: https://huggingface.co/datasets/JuDDGES/pl-nsa
- JuDDGES/pl-nsa-enriched: https://huggingface.co/datasets/JuDDGES/pl-nsa-enriched
- Projekt JuDDGES: https://juddges-project.eu/outputs/
- robots.txt CBOSA: https://orzeczenia.nsa.gov.pl/robots.txt
- Scraper obchodzący blokady (ODRZUCONY): https://github.com/ad-m/cbosa
