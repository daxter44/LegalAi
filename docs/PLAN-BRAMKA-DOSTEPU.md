# Bramka dostępu (3.7) — kody zaproszeń + twardy cap kosztów LLM

Zamknięty test dla prawników wymaga dwóch rzeczy przed deployem (E6): kontroli, KTO wchodzi,
i twardego limitu, ILE może kosztować. Zakres minimalny — bez pełnego ASP.NET Identity.

## Jak działa

- **`Access:Enabled=false` (domyślnie)** — wszystko działa jak dotąd (dev/M4 bez zmian).
  Bramkę włącza się dopiero w deployu.
- **Wejście:** `/wejscie` — statyczny formularz kodu zaproszenia. Poprawny kod → cookie
  (`praworag.auth`, 30 dni, sliding) z tożsamością testera. `/wyjscie` — wylogowanie.
- **Kody per OSOBA** (`Access:Invites`: kod → nazwa testera). Nazwa staje się `UserId` —
  rozmowy, feedback i limity są per tester. Kody podawać przez env, NIE commitować:
  ```bash
  Access__Enabled=true \
  Access__Invites__k7f2m9="Jan Kowalski" \
  Access__Invites__x3p8w1="Anna Nowak" \
  dotnet run --project src/PrawoRAG.Api
  ```
- **Gate UI:** niezalogowany na `/` → 302 na `/wejscie` (cookie handler).
- **Gate API (`/api/chat`, `/api/search`):** cookie ALBO nagłówek **`X-Invite-Code`**
  (wygoda curl/runbooków); inaczej 401 (bez redirectu na HTML).
  ```bash
  curl -s localhost:5024/api/chat -N -H 'content-type: application/json' \
    -H 'X-Invite-Code: k7f2m9' -d '{"question":"..."}'
  ```

## Twardy cap kosztów (`CostGuard`)

Działa OBOK `RateGuard` (okno minutowe) — oś dzienna, klucz = data UTC:

| Limit | Konfiguracja | Domyślnie |
|---|---|---|
| zapytania/dzień per tester | `Access:MaxUserRequestsPerDay` | 50 |
| zapytania/dzień globalnie | `Access:MaxGlobalRequestsPerDay` | 300 |
| znaki wyjścia LLM/dzień globalnie (proxy tokenów) | `Access:MaxGlobalOutputCharsPerDay` | 2 000 000 |

Enforcement w OBU torach (UI/`Chat.razor` + SSE/`/api/chat`); komunikat mówi, który limit padł.
**Świadome ograniczenie:** liczniki in-memory — restart aplikacji zeruje dzień (dla zamkniętego
testu kilku osób akceptowalne; persystencja w DB gdy test urośnie).

## Bezpieczeństwo — świadome decyzje

- `POST /wejscie` ma `DisableAntiforgery` (statyczny formularz bez tokenu) — login-CSRF przy
  kodzie zaproszenia to ryzyko pomijalne.
- Kody porównywane case-sensitive, z trimem. Pole formularza typu `password` (nie zostaje
  w podpowiedziach), ale kody NIE są hasłami — rotować po teście.
- Rate-limiter HTTP (60/min) obejmuje też `/wejscie` — zgadywanie kodów jest tępione.

## Runbook: włączenie na betę + weryfikacja izolacji (P0-3, 2026-07-21)

Kod izolacji jest kompletny i przetestowany (`AccessGateTests`; `ConversationStore` filtruje po
`UserId`; obwód Blazora bierze tożsamość z `AuthenticationStateProvider`). Do włączenia = konfiguracja
+ weryfikacja, BEZ zmiany dev-owego `Access:Enabled=false` w `appsettings.json`.

1. **Env deployu** (kody NIE commitowane): `Access__Enabled=true` + `Access__Invites__<KOD>=<NAZWA>`.
   **Każdy kod = UNIKALNA nazwa** — `UserId` to nazwa, więc dwa kody z tą samą nazwą = współdzielone
   rozmowy i wspólny dzienny limit.
2. **Trwałość logowań (KRYTYCZNE w deployu):** ustawić `DataProtection__KeysPath` na trwały wolumen
   (mechanizm już wpięty, `Program.cs:68`). Bez tego klucze są efemeryczne i **restart aplikacji
   wylogowuje wszystkich / psuje ciasteczka**. W czystym dev bez znaczenia.
3. **Weryfikacja izolacji (kryterium wyjścia, wymaga żywej bazy):**
   - dwa różne kody w dwóch przeglądarkach → każdy widzi WYŁĄCZNIE swoje rozmowy w sidebarze;
   - próba wejścia w cudze `conversationId` (ręcznie) → pusto (filtr `UserId` po stronie serwera);
   - `/wyjscie` czyści sesję; ponowne `/wejscie` innym kodem → inna tożsamość, inne rozmowy;
   - anonimowy `/api/chat` bez cookie/`X-Invite-Code` → 401.

## Poza zakresem (jawnie)

- Pełne ASP.NET Identity / OIDC / rejestracja (FE-5 — po testach zamkniętych).
- Persystencja liczników kosztów w DB.
- Panel administracyjny kodów (env/config wystarczy dla kilku testerów).
