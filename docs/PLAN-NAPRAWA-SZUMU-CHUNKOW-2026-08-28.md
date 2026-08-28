# Plan naprawy trzech źródeł szumu w chunkach (kontynuacja diagnozy z 2026-08-26)

Data: 2026-08-28. Kontynuacja `DIAGNOZA-SZUM-PRZYPISOW-NOWELIZACJI-2026-08-26.md` — tam były
szacunki z próbek i dwie nierozstrzygnięte niewiadome (root cause mojibake, źródło „⚫").
Tutaj: dokładne zliczenia na całym korpusie, rozstrzygnięte root cause obu problemów,
propozycja naprawy z pilotem walidacyjnym i warunkami zabicia. Żadnych zmian w kodzie ani
danych nie wprowadzono.

## Kluczowy wniosek dla decyzji o koszcie

**Pełny re-embedding korpusu NIE jest potrzebny dla żadnego z trzech problemów.** Wszystkie
trzy to backfill podzbioru: łącznie ~74 tys. chunków = **0,9% korpusu** (7,97 mln wierszy).
Cały korpus był embedowany na RTX 3060 — 74 tys. chunków to na tym samym sprzęcie praca
rzędu pojedynczych godzin, nie dni.

## Dokładne zliczenia (pełny skan, nie próbka; baza 192.168.100.11, 2026-08-28)

| Problem | Skala dokładna | Gdzie | Szacunek z diagnozy 26.08 |
|---|---|---|---|
| P1: przypisy nowelizacyjne (≥5 × `poz. N`) | **14 726 chunków** | akty (ELI+EURLEX) | 14 724 — trafiony |
| P2: glif „⚫" | **35 197 chunków** | **wyłącznie SAOS/judgment** (0,5% SAOS) | ~16–18 tys. — 2× niedoszacowany; „0 w aktach" obala domysł o formularzach aktów |
| P3: mojibake `[∏Ê˝ƒ]` | **23 891 chunków / 1 906 dokumentów** | 23 870 w ELI/act (4,4% chunków ELI), 113 SAOS, 21 EURLEX, 16 NSA | ~24–25 tys. — trafiony |

```sql
-- zapytanie użyte do zliczeń (pełny skan z JOIN documents, statement_timeout 560s wystarczył)
SELECT count(*) FILTER (WHERE c."Text" ~ '[∏Ê˝ƒ]'),
       count(*) FILTER (WHERE c."Text" LIKE '%⚫%'),
       count(*) FILTER (WHERE (SELECT count(*) FROM regexp_matches(c."Text",'poz\.\s*\d+','g')) >= 5)
FROM chunks c JOIN documents d ON d."Id"=c."DocumentId" WHERE d."DocType"='act';
```

## Rozstrzygnięte niewiadome z diagnozy 26.08

### P3 — root cause mojibake: PDF (font encoding), NIE HTTP

- Rozkład dotkniętych dokumentów po latach: praktycznie w całości **DU 2000–2009**
  (62–291 dokumentów/rok), po 2009 pojedyncze sztuki. To akty „born-digital PDF" bez wersji
  HTML w ISAP — connector (`EliSejmConnector.HasHtmlAsync`) kieruje je na ścieżkę
  `FetchPdfAsync` → `PdfPigTextExtractor`.
- Podmiany glifów są **deterministyczne i czytelne w danych**: `∏`→ł, `Ê`→ś, `˝`→ż, `ƒ`→ń,
  `à`→ą, `´`→ę, `ç`→ć, `ê`→ź („wartoÊciowych"=„wartościowych", „Je˝eli"=„Jeżeli",
  „zadaƒ"=„zadań", „dêwi´ku"=„dźwięku"). To klasyczny objaw uszkodzonej/brakującej mapy
  ToUnicode w fontach starych PDF-ów Dz.U. — PdfPig odczytuje kody glifów przez złe mapowanie.
- **Konsekwencja: re-ingestia NIC nie da.** Błąd siedzi w plikach źródłowych PDF (fonty), nie
  w transporcie HTTP — ponowne pobranie i ekstrakcja tym samym torem odtworzy identyczne
  uszkodzenie. `EliSejmConnector` jest niewinny (hipoteza HTTP z diagnozy 26.08 — do odrzucenia).
- Właściwa naprawa: **odwrotna transkodyzacja** (mapa znaków) na tekście już wyekstrahowanym —
  wbrew obawie z diagnozy 26.08 jest tu bezpieczna, bo podmiany są jednoznaczne: znaki
  `∏ Ê ˝ ƒ ´` nie występują w naturalnym polskim tekście prawnym (ryzyko fałszywej podmiany
  dotyczy tylko `à ç é ê` w obcych nazwiskach/zwrotach — dlatego mapę stosować wyłącznie do
  dokumentów zidentyfikowanych sygnaturą `[∏Ê˝ƒ]`, nie do całego korpusu).
- **Znane ograniczenie (uczciwie):** transkodyzacja naprawia litery, ale NIE naprawi dwóch
  pozostałych artefaktów tej samej ekstrakcji: sklejonych spacji („Wprzypadku", „wczasie")
  i łamania wyrazów z przenoszeniem („po-winny", „wy-przedzaç"). De-hyphenacja (sklejanie
  `słowo-\n?ciąg` po transkodyzacji) jest tania i warta dołożenia; sklejone spacje zostaną.
  Czyli poprawa częściowa — pilot (niżej) zmierzy, czy wystarczająca.

### P2 — root cause „⚫": bullety list w HTML orzeczeń SAOS

Podgląd chunków: „⚫" występuje jako **samodzielna linia-marker listy wyliczeniowej** w
uzasadnieniach wyroków (np. lista kwot/zarzutów), między elementami merytorycznymi. Zero
wystąpień w aktach — domysł z diagnozy 26.08 (formularze aktów) był błędny; źródłem jest
`Saos/HtmlText.ToPlainText`, które przepisuje tekst `li`/`p` bez czyszczenia znaków
dekoracyjnych. Naprawa trywialna: usunięcie znaku (i pokrewnych bulletów: `●`, `•` gdy stoją
samodzielnie) w `HtmlText.Normalize` + backfill regex-strip 35 197 chunków.

### Korekta diagnozy 26.08 — cyrylica to NIE mojibake

Dokument-nośnik `019f579c-…` (DU/2026/489, wzór formularza wniosku o ochronę międzynarodową)
zawiera cyrylicę jako **prawdziwą treść**: to trójjęzyczny (PL/EN/RU) formularz urzędowy
(„заявитель", „печать органа"). Sekcja „[HIPOTEZA OBALONA]" z 26.08 obaliła właściwą hipotezę,
ale zastąpiła ją błędną — cyrylica nie jest wariantem uszkodzenia kodowania, tylko legalną
treścią załączników. Takich chunków NIE należy „naprawiać". (Osobna kwestia, poza zakresem:
czy wielojęzyczne załączniki-formularze w ogóle powinny być embedowane.)

## Proponowana naprawa (kolejność wg sygnału na akt)

Wspólny wzorzec dla wszystkich trzech: (a) poprawka w normalizatorze — żeby przyszłe ingesty
nie odtwarzały problemu, (b) backfill podzbioru: `UPDATE chunks."Text"` → `SearchVector`
przeliczy się sam (kolumna GENERATED ALWAYS — zweryfikowane w schemacie), przeliczyć
`TokenCount`, ponowny embedding tylko dotkniętych chunków (`TeiEmbeddingProvider`).

1. **P3 mojibake** (23,9 tys. chunków, 1 906 dokumentów, wyłącznie akty — najbardziej psuje
   dokładnie ten typ dokumentu, na którym stoi wyszukiwanie przepisów):
   - mapa transkodyzacji wyprowadzona z par znalezionych w danych + walidacja słownikowa
     (po transkodyzacji odsetek słów spoza polskiego słownika musi SPAŚĆ — automatyczny test),
   - zastosowana po `PdfPigTextExtractor`/w `ActTextParser` (przyszłość) + backfill (przeszłość),
   - de-hyphenacja jako część tego samego przejścia.
2. **P1 przypisy** (14,7 tys. chunków): detekcja = kotwica frazy
   `zmian(y|a) (tekstu jednolitego )?wymienionej ustawy zostały ogłoszone w` LUB gęstość ≥5 ×
   `poz. N`; blok wycinany z `Text` (propozycja: wycięcie całkowite — historia nowelizacji jest
   odtwarzalna z ISAP, nie ma dziś UI, które by ją pokazywało; przeniesienie do metadanych można
   dorobić później, gdy powstanie potrzeba). Poprawka w `ActNormalizer` (HTML) i
   `ActTextParser` (PDF) + backfill.
3. **P2 „⚫"** (35,2 tys. chunków, tylko orzeczenia): strip w `HtmlText` + backfill. Największy
   liczbowo, ale najmniejszy jednostkowo (marker to 1 znak/linia w długim uzasadnieniu) —
   dlatego ostatni.

## Pilot walidacyjny PRZED backfillem — potwierdzenie, że to w ogóle pomaga

Zgodnie z zasadą „metryka wyniku przed kosztowną pracą". Pilot nie dotyka produkcji:

1. Wylosować ~300 dotkniętych chunków per problem; przygotować wersję oczyszczoną.
2. Policzyć embedding oczyszczonej wersji (TEI, ta sama kolejka co zwykle) do tabeli-cienia.
3. Miara A (tania, automatyczna): dla ~30 zapytań syntetycznych zbudowanych z treści
   merytorycznej chunka (np. parafraza przepisu bez cytowania) porównać cosine
   zapytanie→chunk_brudny vs zapytanie→chunk_czysty oraz RANK chunka względem pełnego top-K
   produkcyjnego indeksu.
4. Miara B (właściwa, na żywym systemie): dla mojibake — kilka pytań użytkownika o treść
   przepisów z dotkniętych rozporządzeń (BHP przy robotach budowlanych DU/2003/401, żegluga
   DU/2003/2072 — realne, znalezione nośniki) przed/po podmianie embeddingu pilotowej próbki.
5. **Warunek zabicia:** jeżeli mediana poprawy ranku na pilocie jest w granicach szumu
   (porównywać z marginesem, nie ostrym `>` — sygnały cosine są zaszumione), backfillu danego
   problemu NIE robić; poprawkę normalizatora można mimo to wdrożyć (koszt ~zero, chroni
   przyszłe ingesty).

## Bezpieczeństwo wykonania

- Przed dotknięciem normalizatorów i przed backfillem: sprawdzić, że ingestia nie działa w tle
  (zasada projektu — nie ruszać ścieżki fetch/resume w trakcie działania).
- Backfill w transakcjach porcjami (np. 1 000 chunków), z zapisem `EmbeddedWith` — żeby dało
  się odróżnić chunki przeliczone od starych i wznowić po przerwaniu.
- Kopia dotkniętych wierszy (Id, Text, Embedding) do tabeli backupowej przed UPDATE — rollback
  bez odtwarzania z ingestii.

## Otwarte decyzje (do rozstrzygnięcia przez właściciela projektu)

1. ~~Czy robić pilot walidacyjny czy od razu backfill~~ — ROZSTRZYGNIĘTE: użytkownik wybrał
   backfill od razu (2026-08-28), pilot pominięty świadomie.
2. ~~Czy P1 wycinać całkowicie~~ — ROZSTRZYGNIĘTE: wycięcie całkowite (adres pierwotny zostaje).
3. Czy wielojęzyczne załączniki-formularze (cyrylica/EN) zostają w indeksie — osobny temat,
   nie blokuje niczego powyżej. NADAL OTWARTE.

## WYKONANIE — 2026-08-28, zakończone

Kod: `Cleaning/MojibakeTranscoder.cs` (mapa Mac-CE↔MacRoman, komplet polskich liter + de-hyphenacja),
`Cleaning/AmendmentFootnoteCleaner.cs` (ciągi ≥5 pozycji, adres pierwotny zostaje, wariant z frazą
„zmiany … ogłoszone" leci w całości), `Cleaning/BulletCleaner.cs`; wpięte w `ActTextParser` (mojibake
+ przypisy, ścieżka PDF), `ActNormalizer` (przypisy, HTML) i `Saos/HtmlText` (bullety) — przyszłe
ingesty nie odtworzą problemów. Backfill: `NoiseBackfillRunner` (tryb `backfill-noise`, keyset po Id,
selekcje samowygaszające, backup do `chunk_noise_backup` przed każdym UPDATE, command timeout 20 min).

Wyniki na żywej bazie (192.168.100.11):

| Problem | Oczyszczono | Zostawiono świadomie | Weryfikacja po |
|---|---|---|---|
| mojibake | 24 019 chunków | — | `Text ~ '[∏Ê˝ƒ]'` → **0** |
| przypisy | ~10 700 chunków | ~4,6 tys. z pozycjami rozproszonymi w treści (nie ciąg) + 115 chunków-czystego-szumu (po czyszczeniu <20 znaków — do ew. przeglądu) | selekcja stabilna |
| bullety | 52 320 chunków | — | `Text ~ '[⚫●•▪◦⬤]'` → **0** |

Backup: 85 491 oryginałów w `chunk_noise_backup` (Id, Text, TokenCount, Embedding, EmbeddedWith,
Problem) — rollback = UPDATE z backupu, bez ingestii. Testy: 886/886 (w tym czyszczenia na realnych
przykładach z bazy). Wpadki po drodze (obie naprawione w kodzie): `Ingestion:MaxItems=3` z appsettings
po cichu ucinał backfill po jednej partii (teraz osobny `Backfill:MaxChunks`); selekcja footnotes
z `regexp_matches` przekraczała pod koniec domyślne 30 s Npgsql.

NIE zmierzono wpływu na jakość retrievalu (pilot pominięty decyzją) — pierwszy sygnał da golden set
/ realne pytania bety; chunki przeliczone można odróżnić po świeżym `EmbeddedWith` i backupie.
