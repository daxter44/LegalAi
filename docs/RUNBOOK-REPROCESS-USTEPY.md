# Runbook: pełny reprocess ustaw pod podział na ustępy (unit_pass)

Data: 2026-08-31. Kontekst: `DIAGNOZA-NAJEM-LOKATOR-CHUNK-ROZMYCIE-2026-08-31.md` (sekcje ANALIZA
UZUPEŁNIAJĄCA + PILOT). Decyzja właściciela: pełny rollout. Kod: tryb `reprocess-ustepy`
(`UstepReprocessRunner`), zweryfikowany smoke'iem na 2 aktach i pilotem na DU/2001/733.

## Co robi

Dla każdego aktu ELI z torem HTML, który ma długi (≥400 tok) chunk artykułowy bez ustępu
(**2 316 aktów** wg stanu na 2026-08-31): backup chunków → `Status='Fetched'` → świeży fetch z ELI
(konektor sam wybiera najnowszy tekst jednolity) → pełny pipeline (normalize z `unit_pass` +
chunk + embed + transakcyjna podmiana chunków) → wpis do checkpointu. Akty toru PDF poza zakresem
(parser PDF nie dzieli ustępów — osobna decyzja).

## Skala i szacunek czasu

- Cele: 2 316 aktów, dziś ~184 tys. chunków / ~42 mln tokenów; po podziale spodziewane ~3–4×
  więcej chunków (pilot: 104→381).
- Czas: dominuje embedding na 3060 + sekwencyjny fetch z api.sejm.gov.pl (delay 250 ms/akt).
  Smoke: 2 akty (41+244 chunków wynikowych) ≈ pojedyncze minuty → całość realnie **liczona
  w godzinach do ~doby**. Run jest wznawialny — można przerwać i dokończyć.

## Prekondycje (sprawdź PRZED startem)

1. **Żadna inna ingestia nie działa** (`ps aux | grep PrawoRAG.Ingestion`, świeżość logów).
2. TEI żywy: `curl -s http://192.168.100.11:8080/info | head -c 100`.
3. Miejsce w bazie: chunki aktów urosną ~3×; backup dokłada kopię starych (~184 tys. wierszy
   z wektorami) w `reprocess_ustepy_backup`.
4. **Golden set BASELINE** przed startem (porównasz po):
   ```bash
   cd ~/PrawoRAG/src/PrawoRAG.Eval
   export ConnectionStrings__Db="Host=192.168.100.11;Port=5432;Database=praworag;Username=praworag;Password=praworag"
   export Embeddings__BaseUrl=http://192.168.100.11:8080
   dotnet run -c Release 2>&1 | tee /tmp/golden-przed-ustepy.log
   ```

## Uruchomienie

```bash
cd ~/PrawoRAG && git pull
cd src/PrawoRAG.Ingestion
export ConnectionStrings__Db="Host=192.168.100.11;Port=5432;Database=praworag;Username=praworag;Password=praworag"
export Embeddings__BaseUrl=http://192.168.100.11:8080
export Ingestion__Mode=reprocess-ustepy
# checkpoint na dysku trwałym — na nim stoi wznawialność:
export Reprocess__CheckpointFile="$HOME/PrawoRAG/logs/reprocess-ustepy.done"

# 1) SMOKE na 5 aktach (obowiązkowo — potwierdza środowisko):
Ingestion__MaxItems=5 dotnet run -c Release

# 2) Pełny bieg (nohup/screen — liczony w godzinach; log co 25 aktów):
nohup dotnet run -c Release > "$HOME/PrawoRAG/logs/reprocess-ustepy.log" 2>&1 &
tail -f "$HOME/PrawoRAG/logs/reprocess-ustepy.log" | grep -E "postęp|Failed|DONE"
```

**Przerwanie/wznowienie:** Ctrl+C / kill w dowolnym momencie; ponowne uruchomienie tej samej
komendy pomija akty z checkpointu. Akt przerwany W TRAKCIE zostaje ze `Status='Fetched'` i starymi
chunkami (spójny — podmiana jest transakcyjna); kolejny run go dokończy.

## Weryfikacja PO

```sql
-- oczekiwane: ustawy z podziałem >> 536 (baseline), bez podziału ~0 w torze HTML
SELECT (count(*) FILTER (WHERE ma_ust)) AS z_podzialem,
       (count(*) FILTER (WHERE NOT ma_ust)) AS bez_podzialu
FROM (SELECT c."DocumentId", bool_or(c."Locator"->>'Paragraph' IS NOT NULL) AS ma_ust
      FROM chunks c JOIN documents d ON d."Id"=c."DocumentId"
      WHERE d."Source"='ELI' AND d."Title" ILIKE 'ustawa%' GROUP BY 1) t;
-- porazki do obejrzenia:
SELECT "ExternalId", "FailureReason" FROM documents WHERE "Source"='ELI' AND "Status"='Failed' LIMIT 20;
```

1. **Golden set PO** (ta sama komenda co baseline; `tee /tmp/golden-po-ustepy.log`) —
   bramka: brak regresji trafień; spodziewana poprawa na pytaniach o ustawy.
2. Pytanie-nośnik na żywym czacie: „Najemca nie płaci czynszu — czy mogę wypowiedzieć umowę?"
   — odpowiedź powinna cytować art. 11 ustawy o ochronie praw lokatorów (próg TRZECH okresów),
   nie sam KC.
3. `REPROCESS-USTEPY DONE: targets=2316 done=… failed=…` — `failed` obejrzeć w logu; pojedyncze
   porażki fetchu są wznawialne tym samym biegiem (nie weszły do checkpointu).

## Rollback (per akt, bez ingestii)

```sql
BEGIN;
DELETE FROM chunks WHERE "DocumentId" = (SELECT "Id" FROM documents WHERE "ExternalId"='DU/XXXX/YYY' AND "Source"='ELI');
INSERT INTO chunks SELECT * FROM reprocess_ustepy_backup
 WHERE "DocumentId" = (SELECT "Id" FROM documents WHERE "ExternalId"='DU/XXXX/YYY' AND "Source"='ELI');
COMMIT;
```
Backup (`reprocess_ustepy_backup`) i pilotowy (`pilot_uopl_chunks_backup`) trzymać do akceptacji
golden setu; potem `DROP TABLE`.

## Znane drobiazgi

- Konektor pobiera NAJNOWSZY tekst jednolity — akty, którym od czasu pierwotnej ingestii przybyła
  konsolidacja, przy okazji się ZAKTUALIZUJĄ (pożądany efekt uboczny; pilot: treść z DU/2023/725).
- `HNSW` indeksuje nowe wektory na bieżąco (pgvector) — bez ręcznego reindeksu.
- Po całości warto odpalić `VACUUM ANALYZE chunks;` (dużo podmian wierszy).
