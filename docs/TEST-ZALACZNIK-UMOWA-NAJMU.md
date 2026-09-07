# Test załącznika (DOC-6): umowa najmu z wbudowanymi wadami + scenariusz pytań

Wniosek z porażki umowy B2B (raport odmów, Case 2): pytanie oceniające („czy umowa chroni interes?")
nie ma kotwicy w żadnej klauzuli (doc-retrieval nie wie, czego szukać), a B2B to swoboda umów — mało
twardego prawa do zacytowania. Dobry test = dziedzina gęsto pokryta prawem bezwzględnie obowiązującym
+ pytania PUNKTOWE + dokument z WBUDOWANYMI, znanymi naruszeniami (test z kluczem odpowiedzi).

## Przygotowanie
Skopiować poniższą umowę do Worda/Pages → zapisz jako PDF (będzie born-digital, przejdzie bramkę
skanów). Uruchamiać z `PRAWORAG_DUMP_PROMPT=1`, żeby widzieć realny skład promptu.

---

## Dokument testowy (treść umowy)

> **UMOWA NAJMU LOKALU MIESZKALNEGO**
>
> zawarta w dniu 1 sierpnia 2026 r. w Poznaniu pomiędzy: Janem Kowalskim (Wynajmujący)
> a Anną Nowak (Najemca).
>
> **§1. Przedmiot najmu.** Wynajmujący oddaje Najemcy do używania lokal mieszkalny nr 4 przy
> ul. Kwiatowej 15 w Poznaniu, o powierzchni 48 m², wraz z wyposażeniem opisanym w załączniku nr 1.
>
> **§2. Cel najmu.** Lokal będzie wykorzystywany wyłącznie na cele mieszkaniowe Najemcy.
>
> **§3. Czas trwania.** Umowa zostaje zawarta na czas oznaczony 24 miesięcy, od 1 września 2026 r.
> do 31 sierpnia 2028 r.
>
> **§4. Czynsz.** Czynsz najmu wynosi 3 200 zł miesięcznie, płatny z góry do 10. dnia każdego
> miesiąca na rachunek bankowy Wynajmującego. Oprócz czynszu Najemca ponosi opłaty za media
> według wskazań liczników.
>
> **§5. Kaucja.** Najemca wpłaca kaucję zabezpieczającą w wysokości **80 000 zł (dwudziestopięciokrotność
> miesięcznego czynszu)**, płatną przed wydaniem lokalu. Kaucja nie podlega oprocentowaniu.
>
> **§6. Wydanie lokalu.** Wydanie nastąpi protokołem zdawczo-odbiorczym w dniu 1 września 2026 r.
>
> **§7. Podwyższanie czynszu.** Wynajmujący może **podwyższyć czynsz w każdym czasie, ze skutkiem
> natychmiastowym**, zawiadamiając Najemcę e-mailem. Nowa stawka obowiązuje od dnia zawiadomienia.
>
> **§8. Obowiązki Najemcy.** Najemca zobowiązuje się do utrzymywania lokalu w należytym stanie,
> dokonywania drobnych napraw oraz niedokonywania zmian konstrukcyjnych bez zgody Wynajmującego.
>
> **§9. Odpowiedzialność za wady.** Strony zgodnie **wyłączają odpowiedzialność Wynajmującego za
> wszelkie wady lokalu**, w tym wady ukryte, zawilgocenie i niesprawność instalacji, niezależnie
> od chwili ich powstania.
>
> **§10. Kara umowna.** W przypadku **wypowiedzenia umowy przez Najemcę** przed upływem okresu,
> na jaki została zawarta, Najemca zapłaci Wynajmującemu **karę umowną w wysokości 15 000 zł**.
>
> **§11. Zwrot lokalu.** Po zakończeniu najmu Najemca zwróci lokal w stanie niepogorszonym ponad
> normalne zużycie. Kaucja podlega zwrotowi w terminie 6 miesięcy od opróżnienia lokalu, po
> potrąceniu należności Wynajmującego.
>
> **§12. Postanowienia końcowe.** W sprawach nieuregulowanych stosuje się przepisy Kodeksu
> cywilnego. Zmiany umowy wymagają formy pisemnej pod rygorem nieważności.

### Klucz — wady wbudowane celowo
| § | Wada | Norma, którą system powinien przywołać |
|---|---|---|
| §5 | kaucja 25× czynszu | ustawa o ochronie praw lokatorów art. 6 ust. 1 — **max 12-krotność** |
| §7 | podwyżka w każdym czasie, natychmiast | uopl art. 8a — wypowiedzenie stawki na piśmie, **3 mies. naprzód** |
| §9 | wyłączenie odpowiedzialności za wady | KC art. 664 (rękojmia najmu), art. 58 § 1 KC |
| §10 | kara umowna za wypowiedzenie | KC art. 483 § 1 — kara umowna tylko dla zobowiązań **niepieniężnych** (+ orzecznictwo o karach przy najmie) |
| §11 | zwrot kaucji w 6 mies. | uopl art. 6 ust. 4 — **1 miesiąc** od opróżnienia lokalu |

---

## Scenariusz pytań (od izolacji do integracji; każde w NOWEJ rozmowie)

**T1 — czysto faktograficzne (izoluje doc-retrieval, bez prawa):**
*„Jaka jest wysokość kaucji i termin jej zwrotu według tej umowy?"*
Oczekiwane: odpowiedź z [D] (§5, §11), bez wymysłów.
⚠ UWAGA — to też test ZNANEJ luki projektowej: bramka abstynencji liczy się z KORPUSU, a pytanie
czysto dokumentowe może retrievować słabo → możliwa odmowa progu MIMO załącznika. Jeśli tak się
stanie, to nie bug implementacji, tylko decyzja projektowa do rewizji (v1.1: czy pytanie z
załącznikiem powinno omijać/luzować bramkę korpusową?). Zanotować wynik.

**T2 — fakt + prawo (główny scenariusz wartości):**
*„Czy kaucja określona w tej umowie jest zgodna z prawem?"*
Oczekiwane: konkluzja wprost („niezgodna"), §5 jako [D], art. 6 uopl jako [n], oba klikalne.

**T3 — fakt + prawo, druga wada:**
*„Czy wynajmujący może podnosić czynsz w sposób opisany w tej umowie?"*
Oczekiwane: „nie" + §7 [D] + art. 8a uopl [n].

**T4 — granica fragmentów (test reguły D3):**
*„Czy ta umowa pozwala najemcy trzymać zwierzęta w lokalu?"*
Oczekiwane: model mówi WPROST, że dołączone fragmenty tego nie regulują — bez zgadywania
i bez frazy odmowy prawnej.

**T5 — kontrola negatywna (świadome powtórzenie wzorca B2B):**
*„Czy ta umowa dobrze chroni interes najemcy?"*
Oczekiwane po naprawach: przy 5 wbudowanych wadach i gęstym prawie najmu system MA szansę wskazać
2-3 naruszenia — ale jeśli znów odmówi, porównanie zrzutu promptu T5 vs T2 pokaże, czy problem
leży w doborze fragmentów pod pytania oceniające (hipoteza 1 z Case 2), czy w zachowaniu modelu.

**T6 — follow-up z dokumentem (życie załącznika w rozmowie):**
Po T2, w tej samej rozmowie: *„a co z karą umowną z §10?"*
Oczekiwane: świeży doc-retrieval trafia §10, odpowiedź z art. 483 KC.

## Co zbierać przy każdym pytaniu
1. Zrzut promptu (`PRAWORAG_DUMP_PROMPT=1`) — które fragmenty [D] weszły (czy trafne §).
2. Odpowiedź: konkluzja wprost? cytuje [D] i [n]? klikalne?
3. Panel „Twój dokument" — czy karty [D] odpowiadają cytowaniom.
4. `citationCheck` — czysty?
5. Przy odmowie: progu (baner) czy treściowa (fraza w tekście)?

## Warunki wstępne
- Korpus musi mieć ustawę o ochronie praw lokatorów (discovery 13 988 aktów powinno ją objąć —
  sprawdzić: `SELECT "Title" FROM documents WHERE "DocType"='act' AND "Title" ILIKE '%ochronie praw lokator%'`).
  Jeśli jej nie ma, T2/T3 przesuną się na KC (art. 664/483) — nadal sensowne, ale zanotować.
- Świeży pull z `feat/halfvec-retriever` (naprawa augmentera `fc887d6` — bez niej źródła może
  zaśmiecać ustawa metropolitalna, jak w raporcie odmów).
