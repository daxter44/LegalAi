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

## Poza zakresem (jawnie)

- Pełne ASP.NET Identity / OIDC / rejestracja (FE-5 — po testach zamkniętych).
- Persystencja liczników kosztów w DB.
- Panel administracyjny kodów (env/config wystarczy dla kilku testerów).
