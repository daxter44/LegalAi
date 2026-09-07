# Runbook: uruchomienie analityki Umami na produkcji (US-2.12)

Data: 2026-09-01. Plan i decyzje: `PLAN-US-2.12-UMAMI.md` (D-1: izolacja osobną bazą — zaakceptowana;
D-3: eventy konwersji od razu — wdrożone w kodzie). Kod: `AnalyticsOptions` (ScriptUrl+WebsiteId),
snippet bez banera zgody (cookieless), kontener w `infra/compose.yaml`, atrybuty `data-umami-event`
na CTA. Bez konfiguracji `Analytics` aplikacja nie ładuje żadnych skryptów — deploy sam z siebie
niczego nie zmienia.

## 1. Baza i rola (ISTNIEJĄCY klaster — init-skrypt kontenera tu nie zadziała)

```sql
-- jako superuser (psql do klastra z bazą praworag); silne hasło zamiast :UMAMI_PASSWORD
CREATE ROLE umami LOGIN PASSWORD :'UMAMI_PASSWORD';
CREATE DATABASE umami OWNER umami;
REVOKE CONNECT ON DATABASE praworag FROM PUBLIC;
GRANT  CONNECT ON DATABASE praworag TO praworag;
REVOKE CONNECT ON DATABASE umami    FROM PUBLIC;
GRANT  CONNECT ON DATABASE umami    TO umami;
```

Weryfikacja izolacji (obie próby mają skończyć się „permission denied"):
```bash
psql "host=... user=umami dbname=praworag password=..." -c "select 1"
psql "host=... user=praworag dbname=umami password=..." -c "select 1"
```

## 2. Kontener

```bash
export UMAMI_DB_PASSWORD='<silne hasło z kroku 1>'
export UMAMI_APP_SECRET="$(openssl rand -hex 32)"
podman compose -f infra/compose.yaml up -d umami
# health: curl -s http://localhost:3000/api/heartbeat  → ok
```
Umami sam założy swoje tabele w bazie `umami` przy pierwszym starcie.

## 3. Panel: konto i witryna

1. `http://<host>:3000` → login `admin` / `umami` → **natychmiast zmień hasło**.
2. Settings → Websites → Add website (nazwa: OmniaSI, domena: docelowa) → skopiuj **Website ID**.

## 4. Ekspozycja publiczna (decyzja D-2 — do rozstrzygnięcia przy hostingu)

Skrypt i beacon muszą być osiągalne z przeglądarek użytkowników. Rekomendacja: subdomena
`analytics.<domena>` w reverse proxy → kontener umami:3000 (czysty, jeden origin w CSP).
Do czasu produkcyjnego proxy testy robi się na porcie.

## 5. Konfiguracja aplikacji i restart

```
Analytics__ScriptUrl = https://analytics.<domena>/script.js
Analytics__WebsiteId = <Website ID z kroku 3>
```
Restart API. CSP automatycznie dostaje origin z ScriptUrl (script-src + connect-src).

## 6. Weryfikacja

- devtools na landing/czat/konto: skrypt `script.js` ładuje się, beacon `POST /api/send` = 200,
  **zakładka Cookies: ZERO wpisów od analityki** (warunek prawdziwości polityki cookies §3);
- panel Umami: odwiedziny widoczne; kliknięcie CTA na landingu → event `cta-hero-rejestracja`
  (pozostałe: `cta-nav-rejestracja`, `cta-plan-start`, `cta-plan-pro`, `rejestracja-submit`,
  `konto-checkout`, `upgrade-z-limitu`);
- brak banera cookie w aplikacji (zdemontowany razem z Clarity/GA).

## Rollback

Wyczyść `Analytics__ScriptUrl`/`__WebsiteId` i zrestartuj API — snippet i wpis w CSP znikają,
aplikacja działa jak bez analityki. Kontener można zgasić niezależnie (`compose stop umami`).
