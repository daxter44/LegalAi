# Plan US-2.12: analityka bez cookies — Umami self-hosted (zadania U-1…U-7)

Data: 2026-09-01. Status: **SPISANE DO REALIZACJI — nic nie wdrożone.**
Decyzje właściciela: rezygnacja z GA4 i Clarity na rzecz narzędzia hostowanego u nas
(uczciwość wobec użytkownika, spójność z suwerennością); wybrane narzędzie: **Umami**
(Node + Postgres, MIT, cookieless domyślnie); izolacja od bazy z rozmowami — patrz U-2.

Warunek prawdziwości dokumentów prawnych (`Legal/polityka-cookies.md` §3, polityka prywatności
pkt 10): pomiar zagregowany, bez cookies, bez identyfikowania osób, bez nagrywania sesji.

## Rozstrzygnięcie izolacji (odpowiedź na obawę o współdzielenie bazy)

**Osobna BAZA DANYCH w tym samym klastrze + dedykowana rola** — mocniejsza ściana niż osobny
schemat przy tym samym koszcie: rola `umami` nie dostaje prawa CONNECT do bazy `praworag`
(i odwrotnie), więc kompromitacja kontenera Umami nie daje żadnej ścieżki SQL do treści rozmów.
Schemat zostawiałby wspólną bazę (wspólne CONNECT, ryzyka search_path/grantów). Umami sam
zarządza swoimi migracjami we własnej bazie — zero styku z naszym EF.

## Zadania

### U-1 — kontener Umami w `infra/compose.yaml`
- Usługa `umami`: `ghcr.io/umami-software/umami:postgresql-latest`, `depends_on: db (healthy)`,
  `DATABASE_URL=postgresql://umami:${UMAMI_DB_PASSWORD}@db:5432/umami`,
  `APP_SECRET=${UMAMI_APP_SECRET}` (sekrety w env, poza repo).
- Dev: port `${UMAMI_PORT:-3000}`. Prod: bez publicznego portu — wystawienie przez reverse proxy
  (decyzja D-2 niżej). Healthcheck HTTP `/api/heartbeat`.
- Akceptacja: `podman compose up` stawia Umami, panel loguje się, strona dodana testowo zlicza wejście.

### U-2 — izolacja w Postgresie (SQL, do runbooka — wykonuje właściciel)
```sql
CREATE ROLE umami LOGIN PASSWORD :'umami_password';
CREATE DATABASE umami OWNER umami;
REVOKE CONNECT ON DATABASE praworag FROM PUBLIC;   -- domyślny grant PUBLIC to furtka
GRANT  CONNECT ON DATABASE praworag TO praworag;
REVOKE CONNECT ON DATABASE umami    FROM PUBLIC;
-- (rola umami łączy się tylko z bazą umami; rola praworag nie dostaje grantu do umami)
```
- Dla świeżego klastra (dev): skrypt w `docker-entrypoint-initdb.d` (Dockerfile.db) — uwaga:
  init odpala się WYŁĄCZNIE na pustym wolumenie; istniejące bazy (M4, dev z danymi) = ręczny SQL.
- Akceptacja: `psql -U umami -d praworag` → permission denied; `psql -U praworag -d umami` → j.w.

### U-3 — aplikacja: skrypt Umami zamiast consent.js + Clarity/GA
- `AnalyticsOptions` → `{ ScriptUrl, WebsiteId }` (Enabled = oba niepuste; puste = zero skryptów,
  jak dotąd). `AnalyticsSnippet` renderuje `<script defer src="{ScriptUrl}" data-website-id="…">`
  — **bez bramki zgody** (cookieless: nic nie zapisuje na urządzeniu → poza art. 173 PT).
- USUNĄĆ: `wwwroot/js/consent.js`, baner, cookie `omniasi-consent`, linki „Ustawienia cookies"
  (stopka landingu, karta prywatności /konto), stałe CSP Clarity/GA.
- CSP: host z `ScriptUrl` dołączany do `script-src` i `connect-src` (Umami śle beacon na
  `{origin}/api/send`) tylko gdy Enabled — wzorzec już istnieje.
- Testy: aktualizacja `Analytics_snippet_follows_configuration` + test, że przy Enabled
  snippet NIE zawiera ładowania warunkowanego zgodą.
- Akceptacja: devtools na landing/czat/konto — skrypt się ładuje, **zero cookies** od analityki,
  wejścia widoczne w panelu Umami; bez konfiguracji — zero zmian (dev).

### U-4 — zdarzenia konwersji (bez inline JS)
- Atrybuty `data-umami-event` na CTA: landing („Zacznij za darmo", „Załóż konto", „Wybierz Pro",
  „Przejdź do czatu"), rejestracja (submit), link zakupu przy limicie (US-3.9), /konto („Wykup plan").
- Akceptacja: zdarzenia widoczne w Umami; funnel landing→rejestracja policzalny.
- (Analityka PRODUKTOWA — co się dzieje w czacie — pozostaje w naszej bazie: trasy, odmowy,
  feedback, zużycie. Umami mierzy tylko ruch stron i konwersje.)

### U-5 — spójność dokumentów prawnych
- Zweryfikować, że polityka prywatności pkt 10 i cookies §3 opisują mechanikę Umami zgodnie
  z prawdą (agregacja, dzienna sól bez trwałego identyfikatora, dane wyłącznie na naszym
  serwerze, brak podmiotów trzecich). Poprawki treści, jeśli sformułowania odbiegają.

### U-6 — runbook wdrożenia na produkcję
Kolejność: U-2 SQL → kontener (U-1) → pierwsze logowanie Umami (konto admina, silne hasło) →
dodanie website → `Analytics__ScriptUrl`/`__WebsiteId` do konfiguracji → restart API →
weryfikacje z U-3/U-4. Rollback: wyczyścić `Analytics` w konfiguracji (snippet i CSP znikają),
kontener można zostawić lub zgasić — bez wpływu na aplikację.

### U-7 — sprzątanie
- Grep po `consent.js`, `omniasi-consent`, `clarity`, `googletagmanager`, `cookie-settings` —
  zero śladów w src/. Usunięcie martwych testów banera.

## Decyzje do potwierdzenia przed startem

- **D-1**: izolacja jak wyżej (osobna baza + rola) — czy akceptujesz zamiast schematu?
- **D-2**: ekspozycja panelu/skryptu Umami na prod: subdomena `analytics.[domena]` przez reverse
  proxy (rekomendacja; skrypt first-party-ish, czysty CSP) vs ścieżka na głównej domenie.
  Wiąże się z wyborem reverse proxy w bloku zewnętrznym (hosting) — można odłożyć do U-6.
- **D-3**: czy U-4 (eventy konwersji) wchodzi od razu, czy po deployu MVP.

## Poza zakresem
Session replay / heatmapy — świadomie NIE (polityka cookies §3 wyklucza nagrywanie sesji;
w naszej aplikacji nagranie = treść pytań prawnych). Analityka produktowa = własne dane w bazie.
