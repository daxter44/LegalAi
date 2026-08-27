# Golden-set (E5) — baseline PRZED dociągnięciem korpusu prawa UE (EUR-Lex)

Data: 2026-08-26. Zapisane jako punkt odniesienia — do porównania z tym samym runem PO
zakończeniu ingestii EUR-Lex (Fazy 1–4 już w kodzie na `feat/halfvec-retriever`, ingestia treści
osobno). Zestaw `golden-set.json` zawiera pytania z kategorią `ue-*` (RODO, AI Act, DSA, DMA,
MDR, REACH, MAR, e-privacy i inne) — te pozycje są NAJBARDZIEJ wrażliwe na dociągnięcie realnego
korpusu unijnego, bo dziś oceniają retrieval bez pełnego źródła.

Uruchomienie: `dotnet run --project src/PrawoRAG.Eval` (bez flag — golden-set, retrieval-only,
`--chat` NIE był użyty, więc metryki end-to-end/anty-halucynacja nieaktywne w tym przebiegu).

## Wynik surowy

```
=== RAPORT EWALUACJI (E5) ===
Pozycji: 40   próg abstynencji: 0,00
Recall@K (retrieval): 26%   (na 27 poz. z oczekiwanym źródłem)
Trafność abstynencji: 72%   (na wszystkich 40)
Anty-halucynacja (pułapki): — (czat nieuruchomiony)   (na 0 pułapkach)
Abstynencja END-TO-END (LLM/czat): — (czat nieuruchomiony)   (na 0 poz. z czatem) ← realna bramka
Świeżość (nowela w źródłach): 0%   (na 1 poz. Freshness) ← strażnik regresji AKT
Śr. similarity: w korpusie 0,833 vs poza 0,849  (rozdział -0,016)

Kalibracja progu: najlepszy ≈ 0,30 (trafność abstynencji 72% na golden secie).
```

### Similarity per pytanie (malejąco)

```
0,8793  [OutOfCorpus   ] oczek=ODMOWA    out-morskie
0,8730  [Trap          ] oczek=ODMOWA    ue-trap-95-46
0,8703  [Trap          ] oczek=ODMOWA    trap-kk-999
0,8700  [InCorpus      ] oczek=ODPOWIEDŹ kp-52
0,8689  [Trap          ] oczek=ODMOWA    trap-kpc-9999
0,8684  [Freshness     ] oczek=ODPOWIEDŹ fresh-kpc-nowela
0,8666  [Trap          ] oczek=ODMOWA    ue-trap-rodo-999
0,8617  [InCorpus      ] oczek=ODPOWIEDŹ kc-415
0,8560  [RelatedButWrong] oczek=ODMOWA    related-vat
0,8547  [InCorpus      ] oczek=ODPOWIEDŹ kk-278
0,8511  [InCorpus      ] oczek=ODPOWIEDŹ ue-produkty-5
0,8506  [InCorpus      ] oczek=ODPOWIEDŹ ue-rodo-17
0,8503  [InCorpus      ] oczek=ODPOWIEDŹ ue-mdr-10
0,8487  [InCorpus      ] oczek=ODPOWIEDŹ kpk-41
0,8485  [InCorpus      ] oczek=ODPOWIEDŹ ue-dsm-17
0,8431  [InCorpus      ] oczek=ODPOWIEDŹ uodo-60
0,8429  [InCorpus      ] oczek=ODPOWIEDŹ ue-konsument-9
0,8409  [OutOfCorpus   ] oczek=ODMOWA    out-rodo
0,8402  [Trap          ] oczek=ODMOWA    trap-false-premise
0,8400  [InCorpus      ] oczek=ODPOWIEDŹ uodo-107
0,8393  [InCorpus      ] oczek=ODPOWIEDŹ kk-148
0,8384  [InCorpus      ] oczek=ODPOWIEDŹ konsument-odstapienie
0,8376  [InCorpus      ] oczek=ODPOWIEDŹ ue-zywnosc-9
0,8375  [InCorpus      ] oczek=ODPOWIEDŹ ue-turystyka-12
0,8374  [RelatedButWrong] oczek=ODMOWA    related-pilot
0,8321  [InCorpus      ] oczek=ODPOWIEDŹ ue-dsa-16
0,8316  [InCorpus      ] oczek=ODPOWIEDŹ ue-rodo-33
0,8298  [InCorpus      ] oczek=ODPOWIEDŹ ue-reach-33
0,8263  [InCorpus      ] oczek=ODPOWIEDŹ kro-rozwod
0,8251  [InCorpus      ] oczek=ODPOWIEDŹ lawyer-kredyt-darmowy
0,8217  [InCorpus      ] oczek=ODPOWIEDŹ ue-dma-5
0,8102  [InCorpus      ] oczek=ODPOWIEDŹ ue-eprivacy-5
0,8083  [InCorpus      ] oczek=ODPOWIEDŹ ue-kierowcy-6
0,8067  [InCorpus      ] oczek=ODPOWIEDŹ ue-aiact-5
0,8049  [OutOfCorpus   ] oczek=ODMOWA    ue-out-ccpa
0,8040  [InCorpus      ] oczek=ODPOWIEDŹ ue-aiact-deepfake
0,8033  [InCorpus      ] oczek=ODPOWIEDŹ ue-mar-17
0,8010  [RelatedButWrong] oczek=ODMOWA    ue-related-ukgdpr
0,7955  [InCorpus      ] oczek=ODPOWIEDŹ ue-aiact-50
0,7863  [InCorpus      ] oczek=ODPOWIEDŹ ue-rodo-6

Różnych wartości similarity: 40 / 40  (gdyby 1 → BUG: score nie zależy od pytania)
```

### Średnia similarity per kategoria

```
Freshness      : śr=0,8684  min=0,8684  max=0,8684  (n=1)
Trap           : śr=0,8638  min=0,8402  max=0,8730  (n=5)
OutOfCorpus    : śr=0,8417  min=0,8049  max=0,8793  (n=3)
InCorpus       : śr=0,8320  min=0,7863  max=0,8700  (n=28)
RelatedButWrong: śr=0,8314  min=0,8010  max=0,8560  (n=3)

„Mamy odpowiedź" : śr=0,8322  najniższy=0,7863
„Nie mamy"       : śr=0,8490  najwyższy=0,8793
→ NAKŁADAJĄ SIĘ: najniższe „mamy" (0,7863) ≤ najwyższe „nie mamy" (0,8793) — brak czystego progu,
  bramka na etapie LLM (--chat).
```

## Do czego to służy

Punkt odniesienia PRZED ingestią treści EUR-Lex. Pozycje `ue-*` (16 z 40 = 40% golden-setu) dziś
oceniają retrieval na korpusie, który może jeszcze NIE mieć pełnej treści aktów UE realnie
zaingestowanej (kod Faz 1–4 jest gotowy — odkrywanie, pobieranie, normalizacja, rozpoznanie
cytatów — ale to nie to samo co potwierdzenie, że dany akt/artykuł już fizycznie siedzi w
`chunks`). Po zakończeniu ingestii ten sam eval (identyczna komenda, bez zmian w `golden-set.json`)
powinien pokazać:
- wzrost **Recall@K** ponad 26% — głównie na pozycjach `ue-*` z `oczek=ODPOWIEDŹ`,
- zmianę similarity per pytanie w kategorii `InCorpus` dla pozycji `ue-*` (dziś część z nich
  najniżej w rankingu: `ue-rodo-6` 0,7863, `ue-aiact-50` 0,7955, `ue-mar-17` 0,8033 — kandydaci do
  weryfikacji, czy to był brak treści czy słaby retrieval mimo obecności treści),
- potencjalnie zmianę nakładania się rozkładów „mamy"/„nie mamy" (dziś zachodzą na siebie —
  0,7863 vs 0,8793 — trudno wyznaczyć czysty próg cosine; to i tak docelowo rola bramki LLM
  w `--chat`, nie samego progu retrievalu).

**Zastrzeżenie**: ten przebieg NIE użył `--chat`, więc metryki end-to-end (abstynencja realna,
anty-halucynacja, walidacja cytatów) są nieaktywne w tym baseline. Porównanie po EUR-Lex powinno
rozważyć dołożenie `--chat`, żeby ocenić też tę warstwę, nie tylko surowy retrieval.
