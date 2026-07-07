# Plan implementacji: interfejs demo (FE) — zadania

Warstwa interfejsu, która zamienia RAG obsługiwany `curl`em w **deployowalne demo dla 2–3 zaufanych
prawników**. Dokument planistyczny — do przeglądu PRZED implementacją; nic tu jeszcze nie kodujemy.

## Decyzje (zablokowane)

- **Framework:** Blazor Server, **jeden host** (rozszerzamy `PrawoRAG.Api` o UI) — te same serwisy przez DI,
  bez skoku HTTP; `/api/*` zostaje dla przyszłych klientów.
- **Autoryzacja:** Google OIDC (logowanie kontem Google) + **allowlista e-maili** w configu.
- **LLM na demo:** Bielik lokalny (pakiet Diamond, dane nie opuszczają maszyny). Przełącznik
  `Llm:Provider = local | claude` zostaje (awaryjne przełączenie bez przebudowy).
- **Hosting Bielika:** osobna usługa GPU na **Cloud Run (scale-to-zero)** na czas demo — płacimy tylko za
  zapytania; przy napływie użytkowników migracja na **always-on GPU VM** (ucieczka od zimnych startów).
  Ollama na Cloud Run wystawia API zgodne z OpenAI → integracja tylko przez `Llm:Local:BaseUrl`, bez zmian kodu.
- **Render odpowiedzi:** **markdown sanitizowany** (Markdig z konfiguracją bez surowego HTML).
- **Retencja logów pytań:** **6 miesięcy** na start (rewizja przy wzroście rozmiaru/wolumenu).
- **Skala:** 2–3 użytkowników, niski ruch. Optymalizujemy pod prostotę i bezpieczeństwo, nie pod skalę.

## Zasada nadrzędna: UI = warstwa „bezpiecznej porażki"

Interfejs ma sprawić, że błędna odpowiedź jest **widoczna i weryfikowalna**, nie cicha. Źródła zawsze obok,
dosłowne cytaty, jawny baner „wstępny research do weryfikacji", kontrola cytatów wyeksponowana, feedback
jednym kliknięciem. To jednocześnie mechanizm zbierania danych do strojenia (rosnący golden set).

---

## A. Architektura FE

**A1. Host i projekt.** Blazor Server (interaktywny render po stronie serwera) dodany do `PrawoRAG.Api`.
Uzasadnienie: jeden proces, jeden kontener, streaming natywny (SignalR = kanał czasu rzeczywistego
serwer↔przeglądarka), auth ASP.NET „z pudełka".

**A2. Struktura katalogów** (`PrawoRAG.Api`):
- `Components/` — `App.razor`, `Routes.razor`, `_Imports.razor`
- `Components/Layout/` — `MainLayout`, `NavMenu`, `UserMenu`
- `Components/Pages/` — `Chat`, `History`, `Login`, `AccessDenied`, `Error`
- `Components/Shared/` — komponenty design systemu (patrz B)
- `Components/Chat/` — `MessageBubble`, `SourcePanel`, `SourceCard`, `AnswerBanner`, `FeedbackBar`, `CitationCheckBadge`
- `wwwroot/css/` — tokeny + globalne; CSS isolation per komponent (`*.razor.css`)

**A3. Render mode.** `InteractiveServer` dla stron interaktywnych (czat, feedback). Strony logowania/
błędu statyczne. Prerender wyłączony na stronach za auth (unikamy podwójnego renderu stanu użytkownika).

**A4. Stan.** Stan rozmowy trzymany w komponencie/`Scoped`-serwisie na czas obwodu (circuit); trwała
historia w bazie (patrz E). Bez globalnego store — skala tego nie wymaga.

**A5. Streaming odpowiedzi.** LLM zwraca `IAsyncEnumerable<string>` (już mamy w ścieżce `/api/chat`).
Konsumujemy in-process w komponencie; po każdym tokenie `InvokeAsync(StateHasChanged)`. Region odpowiedzi
jako **ARIA live region** (czytniki ekranu ogłaszają dopływające treści). Anulowanie: `CancellationToken`
wpięty w przerwanie/nawigację.

**A6. Warstwa serwisowa UI.** Cienki `IChatService` (UI) opakowujący istniejący retrieval + LLM + grounding,
zwracający model widoku: `{ answer stream, sources[], citationCheck, abstained }`. UI nie zna szczegółów RAG.

---

## B. Styleguide / system projektowy

**Charakter:** profesjonalny, spokojny, „source-forward" (treść i źródła na pierwszym planie), maksymalna
czytelność długiego tekstu prawniczego. Bez ozdobników. Język PL.

**B1. Podejście techniczne.** Bez ciężkiego frameworka CSS. Warstwa **tokenów w CSS variables** +
CSS isolation per komponent. Spójność przez tokeny, nie przez konwencje w głowie.

**B2. Tokeny projektowe** (`wwwroot/css/tokens.css`):
- **Kolor:** neutralna baza (biel/szarości), jeden kolor akcentu (granat/atrament), semantyczne
  (`--ok`, `--warn`, `--danger` — do statusów pokrycia/odmowy). Kontrast min. WCAG AA (4.5:1 tekst).
- **Typografia:** skala (np. 12/14/16/18/24/32), font systemowy lub jeden czytelny serif/sans;
  interlinia ~1.6 dla długich cytatów.
- **Spacing:** skala 4/8/12/16/24/32/48. **Radius, cień (elevation), z-index** — po jednym zestawie.
- **Motyw:** jasny na start; tokeny przygotowane pod ciemny (bez implementacji teraz).

**B3. Komponenty bazowe** (`Components/Shared/`), każdy z jawnymi stanami (hover/focus/disabled/loading):
`Button` (primary/secondary/ghost/danger), `Badge`/`Chip`, `Card`, `Banner`/`Callout`,
`Spinner`/`Skeleton`, `Icon`, `TextField`/`Textarea`, `Tooltip`, `EmptyState`, `Toast`.

**B4. Dostępność (a11y):** nawigacja klawiaturą (czat wysyłalny `Ctrl+Enter`), widoczny `focus`,
etykiety ARIA, live region na streaming, kontrast AA, `prefers-reduced-motion`. Kryterium: przejście
całego czatu bez myszy.

**B5. Stany widoku (obowiązkowe dla każdej strony):** loading, empty, error, brak-uprawnień, offline/
utrata-obwodu (Blazor „Attempting to reconnect"). Kryterium: żaden ekran nie „wisi" bez komunikatu.

**B6. Responsywność:** priorytet desktop (praca prawnika), ale czytelne na tablecie. Panel źródeł
zwijany na wąskich ekranach.

---

## C. Bezpieczeństwo (przekrojowe — checklist z zadaniami)

**C1. Uwierzytelnianie.** Google OIDC, ciasteczko sesyjne. Cookie: `HttpOnly`, `Secure`, `SameSite=Lax`.
`FallbackPolicy = RequireAuthenticatedUser` (**deny-by-default** — każda strona wymaga logowania, chyba że
jawnie `[AllowAnonymous]`). Allowlista e-maili egzekwowana **po stronie serwera** (claim `email` po callbacku
OIDC; brak na liście → `AccessDenied`), nigdy tylko w UI.

**C2. Autoryzacja / własność danych.** Każde zapytanie o historię/feedback filtrowane po `UserId`
z tożsamości (claim), **nigdy po id z klienta**. Brak dostępu do cudzych rozmów nawet przy zgadniętym id.

**C3. XSS / renderowanie treści.** Odpowiedź LLM renderujemy jako **sanitizowany markdown** (Markdig,
konfiguracja bez surowego HTML — żadnych bloków HTML ani `<script>`). Fragmenty źródeł to już czysty
tekst (normalizery produkują plain text) — renderujemy
z auto-enkodowaniem Blazora. Linki źródłowe: walidacja schematu (`https` do isap/saos), `rel="noopener
noreferrer"`. Zero `MarkupString` na danych pochodzących od LLM/użytkownika.

**C4. CSP i nagłówki.** `Content-Security-Policy` dopasowany do potrzeb Blazora (skrypt frameworka,
websocket dla SignalR); `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY` (anty-clickjacking),
`Referrer-Policy`, HSTS. Kryterium: brak `unsafe-inline` dla skryptów tam, gdzie się da.

**C5. DataProtection.** Klucze ASP.NET DataProtection **trwałe** (wolumen/katalog), inaczej po restarcie
VM sesje/ciasteczka się psują. (Ważne przy jednej VM i przy restarcie kontenera.)

**C6. Sekrety.** Client secret OIDC, connection string DB, ewentualny klucz Claude — z **zmiennych
środowiskowych / menedżera sekretów**, nigdy w repo. `.gitignore` pokrywa `appsettings.*.Local.json`.

**C7. Rate limiting / koszt.** Limit zapytań na użytkownika/min (nawet dla zaufanych — chroni przed
runaway kosztem LLM i pętlą). Limit długości pytania. `RateLimiter` z ASP.NET.

**C8. Prompt-injection (świadomie ograniczone).** RAG gruntuje na źródłach; pytanie użytkownika nie może
zmieniać instrukcji systemowych — utrzymać rozdział ról w promptcie, logować podejrzane wejścia. Pełna
obrona poza zakresem demo, ale rozdział prompt systemowy/użytkownika obowiązkowy.

**C9. RODO / poufność logów.** Pytania prawników mogą zawierać dane osobowe/wrażliwe sprawy. Log pytań =
dane poufne: dostęp tylko dla admina, brak wysyłki do stron trzecich (spójne z wyborem Bielika lokalnego).
**Retencja: 6 miesięcy** (zadanie automatycznego czyszczenia starszych wpisów; rewizja przy wzroście).
Krótka nota prywatności na demo. Kryterium: retencja 6 mies. wdrożona i udokumentowana.

**C10. Obwód Blazor (circuit).** Obsługa utraty połączenia (reconnect UI), limit czasu obwodu, brak
wrażliwych danych w stanie klienta. Autoryzacja sprawdzana serwerowo per akcja, nie tylko przy wejściu.

**C11. Transport.** TLS/HTTPS wymuszony (reverse proxy z auto-certem, np. Caddy/nginx), przekierowanie
80→443, HSTS. Firewall VM: tylko 443 (i SSH z ograniczeniem).

---

## D. Model danych (persystencja demo)

Nowe encje w `PrawoRAG.Storage` (+ migracja EF). **Każdy wiersz z `UserId`** (hak pod user scope).
- **`Conversation`**: `Id`, `UserId`, `Title`, `CreatedAt`, `UpdatedAt`.
- **`Message`**: `Id`, `ConversationId`, `Role` (user/assistant), `Content`, `CreatedAt`,
  `RetrievedSources` (jsonb: lista {docId, locator, similarity}), `Abstained` (bool), `CitationClean` (bool?).
- **`Feedback`**: `Id`, `MessageId`, `UserId`, `Verdict` (up/down/wrong-answer/needless-refusal),
  `Note` (opcjonalny tekst), `CreatedAt`.
Kryterium: z tych tabel da się odtworzyć „pytanie → co zwrócił retrieval → co odpowiedział → ocena
prawnika" = materiał do golden setu i kalibracji.

---

## E. Zadania per epik

Format: `[FE-x.y] tytuł — kryterium akceptacji`.

### FE-0 — Fundament hosta
- [FE-0.1] Dodać Blazor Server do `PrawoRAG.Api` (`AddRazorComponents().AddInteractiveServerComponents`,
  `MapRazorComponents`) — aplikacja startuje, `/api/*` nadal działa. *Kryt.: strona `/` renderuje, `/api/search` odpowiada.*
- [FE-0.2] `MainLayout` + `Routes` + `Error`/`AccessDenied` + healthcheck `/healthz`.
- [FE-0.3] `IChatService` (UI) opakowujący retrieval+LLM+grounding → model widoku. *Kryt.: testowalny bez UI.*

### FE-1 — Styleguide / design system
- [FE-1.1] `tokens.css` (B2) + reset/globalne. *Kryt.: wszystkie kolory/odstępy z tokenów.*
- [FE-1.2] Komponenty bazowe (B3) ze stanami + strona-piaskownica `/_styleguide` (tylko dev). *Kryt.: podgląd wszystkich wariantów.*
- [FE-1.3] A11y baseline (B4): focus, ARIA, klawiatura. *Kryt.: obsługa czatu bez myszy.*

### FE-2 — Czat + streaming
- [FE-2.1] Strona `Chat`: pole pytania, lista wiadomości, `MessageBubble`.
- [FE-2.2] Streaming tokenów z `IChatService` + live region + anulowanie. *Kryt.: odpowiedź „leci" płynnie, da się przerwać.*
- [FE-2.3] Stany: loading/empty/error/reconnect. *Kryt.: żaden nie wisi bez komunikatu.*

### FE-3 — Źródła + warstwa bezpieczeństwa treści
- [FE-3.1] `SourcePanel`/`SourceCard`: sygnatura/artykuł, **dosłowny cytat**, link do ISAP/SAOS (walidowany). *Kryt.: każda odpowiedź ma widoczne źródła.*
- [FE-3.2] `AnswerBanner` „wstępny research do weryfikacji" + stan odmowy („brak wystarczających źródeł").
- [FE-3.3] `CitationCheckBadge` — wynik anty-fabrykacji wyeksponowany (czyste/podejrzane cytaty).
- [FE-3.4] Render odpowiedzi: **markdown sanitizowany** (Markdig bez raw HTML) (C3). *Kryt.: wstrzyknięty HTML/`<script>` nie wykonuje się; formatowanie md działa.*

### FE-4 — Feedback + log pytań
- [FE-4.1] Migracja: `Conversation`/`Message`/`Feedback` (D).
- [FE-4.2] Zapis każdej wymiany (pytanie, źródła, abstained, citationClean) do `Message`.
- [FE-4.3] `FeedbackBar` (👍 / zła odpowiedź / niepotrzebna odmowa + nota) → `Feedback`. *Kryt.: ocena ląduje w bazie z kontekstem.*
- [FE-4.4] Retencja: zadanie czyszczące wpisy starsze niż 6 miesięcy (C9). *Kryt.: stare logi znikają automatycznie.*

### FE-5 — Autoryzacja
- [FE-5.1] Google OIDC + cookie (C1), przyciski logowania/wylogowania, `UserMenu`.
- [FE-5.2] Allowlista e-maili (config) egzekwowana serwerowo → `AccessDenied` poza listą. *Kryt.: spoza listy brak wejścia.*
- [FE-5.3] `FallbackPolicy` deny-by-default; `[AllowAnonymous]` tylko na login/error. *Kryt.: nieuwierzytelniony nie widzi czatu.*

### FE-6 — User scope + historia
- [FE-6.1] `UserId` z claima w kontekście każdej operacji zapisu/odczytu.
- [FE-6.2] `History`: lista rozmów **tylko własnych** (filtr `UserId`, C2). *Kryt.: brak dostępu do cudzych po id.*
- [FE-6.3] `RetrievalScope` (opcjonalny) przeciągnięty przez `IRetriever`/chat, dziś = „wszystko". *Kryt.: rdzeń gotowy na moduły/role bez zmian API.*

### FE-7 — Hardening bezpieczeństwa
- [FE-7.1] CSP + nagłówki (C4). [FE-7.2] DataProtection trwałe (C5). [FE-7.3] Sekrety z env (C6).
- [FE-7.4] Rate limiting + limit długości pytania (C7). [FE-7.5] Rozdział prompt systemowy/użytkownika (C8).
- [FE-7.6] Nota prywatności + decyzja o retencji logów (C9). *Kryt.: przegląd checklisty C zamknięty.*

### FE-8 — Konteneryzacja + deploy
- [FE-8.1] Dockerfile hosta + `compose` (app + Postgres + TEI). [FE-8.2] Reverse proxy z auto-HTTPS (C11).
- [FE-8.3] **Bielik jako osobna usługa GPU na Cloud Run** (Ollama, scale-to-zero, quota GPU/region).
  Konfiguracja `Llm:Local:BaseUrl` → URL Cloud Run. Mitygacja zimnego startu: `min-instances=1` na czas
  sesji demo. *Kryt.: czat odpowiada z Bielika przez Cloud Run.*
- [FE-8.4] Wgranie embeddingów korpusu do Postgresa; backup Postgresa + firewall. *Kryt.: publiczny URL po HTTPS, logowanie Google działa, czat odpowiada z korpusu.*
- [FE-8.5] (przy ruchu) Migracja Bielika na always-on GPU VM — koniec zimnych startów.

---

## F. Kolejność i zależności

- **Deployowalne demo (rdzeń):** FE-0 → FE-1 → FE-2 → FE-3 → FE-4.
- **Dostęp:** FE-5 → FE-6 (auth przed publicznym wystawieniem).
- **Przed wystawieniem publicznym OBOWIĄZKOWO:** FE-7 (hardening) + FE-8 (TLS/deploy).
- Styleguide (FE-1) i bezpieczeństwo (C) są przekrojowe — FE-1 na początku (spójność od startu),
  checklista C zamykana w FE-7, ale zasady (C2/C3) stosowane od pierwszego kodu.

## G. Ryzyka i decyzje

- **Bielik na Cloud Run — zimny start (świadomy trade-off).** Scale-to-zero → pierwsze zapytanie po
  bezczynności ładuje model 11B do VRAM (kilkadziesiąt s – ~min). Akceptowalne na demo; mitygacja
  `min-instances=1` na czas sesji. Wymaga GPU-quota i regionu z GPU na Cloud Run. Migracja na VM przy ruchu (FE-8.5).
  Bielik częściej fabrykuje → FE-3.3 (citationCheck) tym ważniejsze.
- **Hosting aplikacji (Blazor Server)** — stanowy obwód (SignalR) lubi always-on. Cloud Run z `min-instances=1`
  + affinity ALBO mała VM. Do rozstrzygnięcia przy FE-8 (Bielik i tak jest osobną usługą GPU).
- ✅ **Retencja logów: 6 miesięcy** (C9, FE-4.4) — rewizja przy wzroście.
- ✅ **Render: markdown sanitizowany** (Markdig bez raw HTML, C3/FE-3.4).

## Poza zakresem demo (świadomie później)

Rejestracja/zarządzanie użytkownikami, role-UI, moduły podatkowe/ZUS, ciemny motyw, i18n, panel admina,
zaawansowana obrona prompt-injection, autoskalowanie.
