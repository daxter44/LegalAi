# Analiza konkurencji: lexedit.ai (2026-07-19) — gwiazda północna produktu

Deklaracja właściciela projektu: „dążę do takiego produktu jak lexedit.ai". Analiza strony
(stan 2026-07-19) + wnioski dla kolejności prac. Dokument strategiczny — bez zmian w kodzie;
rozjazdy jako action itemy.

## Czym jest lexedit
Asystent AI dla polskich prawników: research (orzecznictwo SN/NSA/powszechne, ISAP/ELI) →
notatki z weryfikowanymi cytatami (klikalne źródła, „kropka w kropkę z uzasadnienia") →
generowanie pism procesowych (pozwy, odpowiedzi, apelacje, odwołania) → moduł SPRAWY
(workspace klienta). Upload PDF/Word/skany (OCR), anonimizacja PESEL/nazwisk PRZED wysłaniem
do modelu, stan prawny na datę, aplikacja mobilna, kalkulatory/wzory/linie orzecznicze.
Model: Claude (Anthropic — amerykańskie API). Freemium: 10 zapytań bez karty.

## Co to oznacza strategicznie
1. **Rynek zwalidowany** — ktoś już sprzedaje dokładnie ten produkt tym samym odbiorcom.
   Pytanie „czy prawnicy chcą" jest zamknięte; zostaje jakość wykonania i wyróżnik.
2. **Nasz wyróżnik jest realny i niekopiowalny bez przebudowy ich stosu**: lexedit wysyła
   dane do US API i sprzedaje anonimizację jako zabezpieczenie; u nas dane w ogóle nie
   opuszczają infrastruktury PL/UE. Obietnica strukturalnie mocniejsza (tajemnica zawodowa,
   kancelarie o podwyższonych wymaganiach) — spójna z filarem §5/§6 PLAN-STRATEGIA-PILOTAZ.
3. **Nasza mapa = ich lejek**: czat-research → kazus (memo) → dokumenty → pisma. Kierunek
   potwierdzony, nic nie wywracamy.
4. **Parytet obietnic anty-halucynacyjnych** („nie wymyśla, powie wprost") — rozstrzyga
   wykonanie: produkt odmawiający 6/10 przegra niezależnie od suwerenności.

## Mapa luk (stan naszego produktu vs lexedit)
| Funkcja lexedit | U nas | Action item |
|---|---|---|
| Research + weryfikowane klikalne cytaty | ✅ core | hartowanie trwa (eval odmów → 10-25%) |
| Uczciwa odmowa | ✅ (za często) | metryka nadrzędna — bez zmian priorytetu |
| Upload PDF | ✅ DOC v1 | OCR i Word: backlog (OCR najwcześniej — realnie blokuje uploady) |
| Analiza sprawy → memo | 🔶 plan KAZ gotowy | NASTĘPNY duży klocek po fazie jakości |
| Generowanie pism | ❌ | po KAZ; ich główny haczyk sprzedażowy („od researchu do pisma") |
| Moduł SPRAWY (workspace) | ❌ | po KAZ/pismach; historia rozmów to zalążek |
| Anonimizacja | 🔶 backlog | u nas WZMOCNIENIE, nie warunek (dane nie wychodzą); przy pismach wróci |
| Stan prawny na datę (historia przepisów) | 🔶 mamy odwrotność | nasz AKT = świeżość (nowele niewchłonięte) — eksponować w marketingu jako przewagę, historię wersji dopisać do backlogu |
| Mobile / kalkulatory / wzory / linie orzecznicze | ❌ | świadomie NIE — funkcje skalowania, nie walidacji |

## Wnioski dla kolejności prac (potwierdzenie + korekty)
1. Bramka nr 1 bez zmian: odsetek odmów (eval `--refusals`, cel 10-25%) — warunek konkurowania.
2. **KAZ awansuje**: to nasz odpowiednik ich lejka research→pismo; pierwszy krok w stronę
   głównej obietnicy sprzedażowej konkurenta. Po domknięciu jakości — przed wszystkim innym.
3. Pozycjonowanie (uzupełnienie filarów z PLAN-STRATEGIA-PILOTAZ §M1): obok „prawdziwe cytaty"
   i „100% polski stos" dochodzi kontrast wprost: „Twoje akta nie wyjeżdżają do amerykańskiej
   chmury — nawet zanonimizowane" oraz świeżość prawa (nowelizacje, których jeszcze nie ma
   w tekstach jednolitych) jako kontrapunkt dla ich „stanu prawnego na datę".
4. Cennik lexedit = przyszła kotwica cenowa druga obok Lex/Legalis (sprawdzić /research/pricing
   przy pracy nad ceną, §M3).
5. Nie gonić peryferiów przed walidacją: mobile, kalkulatory, wzory, linie orzecznicze.
