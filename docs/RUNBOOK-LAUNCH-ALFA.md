# RUNBOOK: launch zamkniętej ALFY (3060 + M4 + tunel)

Data: 2026-07-23. Zamrożony zakres alfy — checklist operacyjny. **Kod jest kompletny**; to co niżej
wykonuje się NA STACKU (nie w repo). Cel alfy: zebrać realny golden set odmów + sygnał wartości od
2–3 przyjaznych prawników. Alfa = TYLKO przyjaźni (model przez Google = US-hosting, patrz krok 3).

## Stan: co gotowe (kod)
Czat z cytatami + uczciwa odmowa · wyszukiwarka `/szukaj` (retrieval-only) · widok dokumentu
`/dokument/{id}` · analiza dokumentów (za flagą) · lane sygnatury · podstawy prawne na karcie ·
konta/izolacja (bramka invite) · pliki wycięte · ostrzeżenie PII · landing · obsługa thinking.

## Krok 1 — infra na Dellu/3060 (RUNBOOK-3060-DOCKER.md)
- `podman compose up -d` (Postgres+pgvector + TEI mmlw); poczekaj na „Ready" w logach TEI.
- Sanity: `curl http://localhost:8080/health` → 200.

## Krok 2 — baza
- `dotnet ef database update --project src/PrawoRAG.Storage` → dojdzie m.in. `AddDocumentCaseNumber`
  (kolumna + indeks + **backfill sygnatur** z istniejącego korpusu — wyszukiwanie po sygnaturze
  działa od razu na obecnych danych).
- Potwierdź brak `Pending`: `dotnet ef migrations list`.

## Krok 3 — model (Gemma-thinking przez Google AI Studio — most tymczasowy)
Env (niecommitowane):
```
Llm__Provider=local
Llm__Local__BaseUrl=https://generativelanguage.googleapis.com/v1beta/openai/
Llm__Local__Model=<gemma-thinking>
Llm__Local__ApiKey=<klucz AI Studio>
Llm__Local__MaxTokens=8000        # thinking zjada budżet — patrz obserwacja odmów
```
US-hosting ⇒ TYLKO przyjaźni testerzy, uprzedzeni. Docelowo EU self-host (Sherlock) — poza alfą.

## Krok 4 — bramka invite (env, niecommitowane)
```
Access__Enabled=true
Access__Invites__<kod1>=tester1
Access__Invites__<kod2>=tester2
Access__Invites__<kod3>=tester3
# limity mają defaulty: 50/user/dzień, 300/global, 2M znaków — dostrój w razie potrzeby
```

## Krok 5 — uruchom + wystaw
- `dotnet run -c Release --project src/PrawoRAG.Api` na M4.
- Tunel (cloudflared/ngrok) → publiczny URL dla testerów. 0 zł serwera.

## Krok 6 — WERYFIKACJA bramki + izolacji (kluczowe)
- [ ] wejście bez kodu → przekierowanie na `/wejscie` (nie wpuszcza);
- [ ] kod `tester1` → wpuszcza; rozmowa/analiza tworzą się pod `tester1`;
- [ ] kod `tester2` → **NIE widzi** rozmów ani analiz `tester1` (izolacja per UserId);
- [ ] błędny kod → czytelna odmowa.

## Krok 7 — smoke E2E na Gemmie (3 ścieżki)
- [ ] **Czat**: pytanie merytoryczne → odpowiedź z KLIKALNYMI cytatami; pytanie spoza korpusu →
      uczciwa odmowa; jeśli model „myśli" → sekcja „🧠 Rozumowanie" (nie surowe `<thought>`);
- [ ] **Wyszukiwarka** `/szukaj`: fraza → pogrupowane karty; klik → `/dokument/{id}` z treścią;
      sygnatura → to orzeczenie na górze; podstawa prawna zwinięta, rozwija się;
- [ ] **Widok dokumentu**: treść czyta się sensownie (bez brzydkich powtórzeń na granicy chunków —
      jeśli są, zgłoś do dostrojenia trimu);
- [ ] **Analiza** (jeśli `Analysis__Enabled=true`): PDF → raport per §.
- [ ] menu: linki nie nachodzą, logo → strona główna.

## Krok 8 — zaproś 2–3 prawników
Uprzedź: (a) tymczasowy US-hosting modelu, (b) brak orzecznictwa NSA (dokładamy), (c) to wstępny
research do weryfikacji, nie porada. Poproś, by ZGŁASZALI odmowy i oceniali odpowiedzi (👍/zła/
niepotrzebna odmowa) — to źródło golden setu.

## Cel danych z alfy
- **Golden set odmów**: które odmowy słuszne (brak źródeł), które fałszywe (źródła były) — z tego
  post-alfa dostroimy prompt pod Gemmę i zweryfikujemy metrykę `--refusals` na REALNYCH pytaniach.
- Log: `logs/refusals-*.jsonl` + feedback w UI (tabela `feedback` / `analysis_unit_feedback`).

## LINIA STOP — poza alfą (nie robić do czasu danych z alfy)
chase 40% odmów · re-tuning promptu pod Gemmę · pełne NSA · indeks przy skali (IVFFlat/kwantyzacja)
· tryb agentowy analizy jako wymóg (zostaje za flagą) · dostrajanie retrievalu w próżni.
