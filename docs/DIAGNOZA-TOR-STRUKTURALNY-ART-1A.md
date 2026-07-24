# Diagnoza: tor strukturalny milczy dla jawnego cytatu (art. 1a u.p.o.l.)

Data: 2026-07-24. Do wykonania NA STACKU (baza na Dellu/3060, `192.168.100.11`). Tylko odczyt.

## Po co to

Realny przypadek: użytkownik pyta „art 1a ustawa o podatkach i opłatach lokalnych", a w 8 źródłach
dostaje **same wyroki SAOS, zero tej ustawy** — mimo że akt (`DU/1991/31`, art. 1a z aktualną
definicją budynku/budowli) JEST w korpusie (zweryfikowane wcześniej, patrz
[PRZYPADEK-BUDOWLA-BUDYNEK-UPOL-2026-07-23.md](PRZYPADEK-BUDOWLA-BUDYNEK-UPOL-2026-07-23.md)).

Dla jawnego cytatu tor strukturalny (`HybridRetriever.StructuralAsync`) wstawia artykuł na **sam
wierzch** ze `Score = double.MaxValue`, PRZED fuzją i orzeczeniami. Skoro aktu nie ma wcale — tor
zwrócił **pustkę**. To nie „akt przegrał ranking" (kwota/poszerzenie puli tego NIE naprawi — art. 1a
jest ~#32 wśród samych aktów, pomiar CIT-2), tylko urwany łańcuch 4 etapów toru strukturalnego:

1. `CitationParser.Parse` → `Article = "1a"` — ✅ (regex łapie „art 1a")
2. `ActHint` → „ustawa o podatkach i opłatach lokalnych" — ✅ **udowodnione testami CIT-1**
3. `ResolveActAsync(hint)` → fuzzy pg_trgm do tytułu aktu — ❓ niesprawdzone na żywo
4. `FetchArticleAsync` → `ArticleNo == "1a" AND ExternalId == <akt z etapu 3>` — ❓ niesprawdzone na żywo

Etapy 1-2 zielone w unit-testach. Zostają **3 albo 4** — oba tylko-DB. Poniższe zapytania rozdzielają,
który z nich zawodzi.

## Jak uruchomić

Z maszyny widzącej bazę (Dell/3060). Wymaga rozszerzenia `pg_trgm` (jest — resolver go używa).

```bash
psql "host=192.168.100.11 port=5432 dbname=<baza> user=<user>"   # hasło zapyta interaktywnie
```

Wklej oba zapytania (nazwę ustawy zostaw małymi literami — dokładnie tak buduje ją `ActHint`).

### ETAP 3 — czy fuzzy resolver trafia we WŁAŚCIWY akt?

```sql
-- Oczekiwane: na górze DU/1991/31 (baza „... o podatkach i opłatach lokalnych."),
-- NIE nowela DU/2024/1757 (której art. 1a to instrukcja zmiany, nie definicja).
SELECT "ExternalId", "Title",
       similarity("Title", 'ustawa o podatkach i opłatach lokalnych') AS sim
FROM documents WHERE "DocType" = 'act'
ORDER BY sim DESC
LIMIT 5;
```

Resolver bierze `FirstOrDefault` (jednego zwycięzcę) z progiem `sim >= 0.15`. Jeśli #1 to nowela albo
`sim` bazowego aktu < 0,15 — **awaria na etapie 3**.

### ETAP 4 — pod jakim DOKŁADNIE `ArticleNo` leży art. 1a?

```sql
-- Porównanie w kodzie to '==' (case-sensitive w Postgresie), nie ILIKE. Parser daje "1a".
-- Jeśli tu zobaczysz "1A", "1 a", "1A)" itp. — dokładne dopasowanie NIE trafia i tor milczy.
SELECT DISTINCT "ArticleNo"
FROM chunks c JOIN documents d ON d."Id" = c."DocumentId"
WHERE d."ExternalId" = 'DU/1991/31' AND "ArticleNo" ILIKE '1%'
ORDER BY 1;
```

## Jak czytać wynik → jaka naprawa

| Wynik | Wniosek | Naprawa (mała, celna) |
|---|---|---|
| Etap 3: #1 ≠ `DU/1991/31` albo bazowy `sim` < 0,15 | resolver wybiera zły akt / odrzuca | tie-break w `ResolveActAsync`: przy remisie trigramów preferuj krótszy tytuł / akt bazowy (nie nowelę) + próg; ~kilka linii + test |
| Etap 4: `ArticleNo` istnieje, ale ≠ dokładnie `"1a"` (inny case/format) | dokładne `==` nie trafia | normalizacja porównania numeru artykułu po obu stronach (parser + zapytanie); ~1 linia + test |
| Etap 3 OK i etap 4 pokazuje dokładnie `"1a"` | łańcuch powinien działać — problem gdzie indziej | wróć z wynikami — dołożę sondę toru strukturalnego (parse → resolve → fetch) end-to-end |

Po wklejeniu wyników wskażę dokładną zmianę. Naprawa dotyczy KAŻDej nie-kodeksowej ustawy cytowanej
z nazwy (Prawo budowlane, Ordynacja, VAT…), nie tylko u.p.o.l. — to systemowy etap, nie jeden akt.
