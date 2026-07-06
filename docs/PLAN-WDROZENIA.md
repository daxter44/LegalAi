# Plan wdrożenia — od teraz do pilotażu z prawnikami

Cel: udostępnić działające rozwiązanie małej grupie zaufanych prawników. Zasada przewodnia:
**najpierw za darmo potwierdzamy, że działa na pełnym zakresie danych (ustawy, kodeksy, rozporządzenia,
orzeczenia różnych poziomów), dopiero potem płacimy i wdrażamy.** Dotąd przetestowaliśmy tylko wąski
wycinek (kilka kodeksów + trochę orzeczeń COMMON).

## Etap 0 — Domknięcie tego, co zmierzyliśmy (minuty, 0 zł)
- Próg odmowy = 0 (retrieval nie odsiewa po similarity — pomiar E5 pokazał, że to nie działa: pułapki
  scorują najwyżej). Reranker wyłączony. Decyzję o odmowie podejmuje LLM (zmierzone: 86% / 100%).
- Efekt: konfiguracja = to, co udowodniliśmy.

## Etap 1 — Walidacja parsowania WSZYSTKICH typów (0 zł, lokalnie) ← brama
„Parsowanie" = zamiana pobranego pliku HTML na czysty tekst pocięty na kawałki. Tu jest ryzyko, nie w wektorach.
- Narzędzie „raport jakości": normalizuje dokument i pokazuje statystyki + próbki tekstu — BEZ embeddingu
  (bez GPU, bez kosztów).
- Pobrać próbki każdego nietkniętego typu: ustawy nie-kodeksowe, rozporządzenia, orzeczenia różnych
  poziomów (apelacyjne/okręgowe/rejonowe/SN). Obejrzeć, poprawić parser gdzie się sypie.
- Efekt: pewność, że masowe pobranie nie zrobi śmieci. BLOKUJE wydatek na embedding/deploy.

## Etap 2 — Zakres korpusu + pełne pobranie na dysk (godziny–doba, 0 zł)
- Decyzja: co bierzemy (wszystkie kodeksy + ustawy w mocy; orzeczenia — które poziomy, ile lat). Rekomendacja
  po Etapie 1, gdy znamy jakość i wolumeny.
- Pobranie wszystkiego na dysk (samo ściąganie, jednorazowe, darmowe; wolne przez limit API).

## Etap 3 — Masowy embedding na wynajętym GPU (raz, godziny, kilka dolarów)
- Najpierw pomiar tempa na próbce → oszacowanie czasu i kosztu.
- Wynajęty komputer z kartą graficzną (RunPod ~0,69 $/h) → przetworzenie całości → zrzut bazy z wektorami.

## Etap 4 — Test jakości na PEŁNYM korpusie (0 zł)
- Rozszerzony golden set + pomiar E5 na pełnych danych: trafność wyszukiwania, poprawność odmowy, brak
  konfabulacji. Ustawienie skalibrowanych parametrów. Potwierdzenie jakości ZANIM pokażemy prawnikowi.

## Etap 5 — Minimalny interfejs dla prawnika (równolegle z 2–4)
- Czat + panel źródeł z klikalnymi cytatami (rdzeń weryfikacji: prawnik sprawdza przez źródła).

## Etap 6 — Wdrożenie na GCP + dostęp (darmowe kredyty $300)
- Baza + API + „maker wektorów" dla zapytań (CPU wystarcza). Wgranie bazy z wektorami z Etapu 3.
- Decyzja: LLM w chmurze — Claude API (grosze/zapytanie, bez GPU; dane pytań wychodzą do dostawcy) vs Bielik
  na GPU (drożej). Rekomendacja na pilotaż: Claude. Bielik zostaje jako „Diamond" lokalnie u klienta.
- Proste logowanie na kod zaproszenia.

## Etap 7 — Bramka prawna i prywatność (przed zaproszeniem ludzi) ← blokuje
- Spisać stanowisko: licencje danych (akty/orzeczenia poza prawem autorskim; prawo sui generis do bazy; ToS
  SAOS) + przepływ danych (jeśli Claude — pytania wychodzą na zewnątrz).

## Etap 8 — Pilotaż
- Zaprosić kilku zaufanych prawników, zebrać feedback (trafność, pomocność), iterować.

---

**Ścieżka krytyczna:** 1 → 2 → 3 → 4 → 6. Interfejs (5) i prawne (7) gotowe przed samym zaproszeniem.
**Równolegle:** 5 (interfejs) niezależny od danych; 7 (prawne) do przemyślenia w tle.
**Największe niewiadome:** jakość parsowania rozporządzeń/innych orzeczeń (rozstrzyga Etap 1); czy LLM dobrze
odmawia na pełnym, większym korpusie (rozstrzyga Etap 4).
