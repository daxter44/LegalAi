// Klikalne cytowania [n] w odpowiedzi czatu: kotwica #src-{anchorId}-{n} wskazuje kartę źródła.
// Delegacja na document (odpowiedzi renderują się dynamicznie ze streamingu — Blazor podmienia DOM);
// preventDefault, bo router Blazora przechwytuje nawigację wewnętrzną i fragment mógłby przeładować trasę.
//
// RED-2.4: karty źródeł żyją w PANELU po prawej i panel pokazuje JEDNĄ turę naraz. Klik chipa [n]
// w starszej odpowiedzi najpierw przełącza panel na tamtą turę — przez programowe kliknięcie kotwicy
// „Źródła (n)" tej wymiany ([data-src-btn], handler Blazora ustawia aktywną turę i otwiera arkusz
// na mobile) — a scroll+podświetlenie czeka, aż Blazor dorenderuje kartę (retry przez kilka klatek).
document.addEventListener('click', function (e) {
    const a = e.target.closest('a.cite');
    if (!a) return;
    const id = (a.getAttribute('href') || '').slice(1);
    if (!id) return;
    e.preventDefault();

    // Przełącz panel na turę klikniętego cytowania (id = src-{anchor}-{n} / docsrc-{anchor}-{n}).
    const msg = a.closest('.msg-assistant');
    const btn = msg && msg.querySelector('[data-src-btn]');
    if (btn) btn.click();

    let tries = 0;
    (function scrollWhenReady() {
        const card = document.getElementById(id);
        if (!card) {
            if (++tries < 40) requestAnimationFrame(scrollWhenReady); // ~0,7 s na dorenderowanie panelu
            return;
        }
        // Sekcja bywa zwinięta (details w kartach) — kotwica do środka zamkniętego <details> nic by nie pokazała.
        const details = card.closest('details');
        if (details) details.open = true;

        card.scrollIntoView({ behavior: 'smooth', block: 'center' });
        // Restart animacji podświetlenia przy ponownym kliknięciu tego samego źródła.
        card.classList.remove('cite-flash');
        void card.offsetWidth;
        card.classList.add('cite-flash');
    })();
});

// --- Eksport raportu analizy (AJ-12) ---
// Wywoływane z Analiza.razor przez IJSRuntime. MUSI być zwykłym plikiem serwowanym z 'self':
// polityka CSP tej aplikacji (Program.cs) to `script-src 'self'` bez 'unsafe-eval', więc interop
// przez window.eval leci EvalError i przycisk nie robi nic.
window.analysisExport = (function () {
    // Zmiany na czas druku wiszą na beforeprint/afterprint, nie na kliknięciu przycisku: dzięki temu
    // dotyczą tak samo Ctrl+P, a DOM wraca do stanu sprzed druku (wcześniej fragmenty zostawały
    // rozwinięte na ekranie na zawsze). Trzy listy = trzy rodzaje cofnięcia.
    var opened = [];   // <details>, które sami otworzyliśmy
    var skipped = [];  // .source-card poza cytowaniami danego fragmentu
    var labels = [];   // [summary, oryginalny tekst] — licznik „Źródła (n)"

    // Kotwice cytowań: MarkdownRenderer robi <a class="cite" href="#src-{anchor}-{n}">, a karta
    // źródła ma id="src-{anchor}-{n}" — więc href bez „#" to dokładnie id karty do zachowania.
    function citedIds(scope) {
        var ids = Object.create(null);
        scope.querySelectorAll('a.cite[href^="#src-"]').forEach(function (a) {
            ids[a.getAttribute('href').slice(1)] = true;
        });
        return ids;
    }

    function applyPrint() {
        revertPrint(); // idempotentnie — druk bywa wywoływany dwa razy pod rząd
        document.querySelectorAll('details.analysis-unit, details.sources').forEach(function (d) {
            if (!d.open) { d.open = true; opened.push(d); }
        });
        document.querySelectorAll('details.sources').forEach(function (box) {
            // Fragment raportu albo tura dopytania — w obu wypadkach cytowania [n] leżą obok
            // sekcji źródeł, w tym samym kontenerze.
            var scope = box.closest('details.analysis-unit') || box.parentElement;
            if (!scope) return;
            var cited = citedIds(scope);
            var filtruj = Object.keys(cited).length > 0;
            var kept = 0;
            box.querySelectorAll('.source-card').forEach(function (card) {
                // Brak choćby jednego [n] w tekście = drukujemy komplet. Pusta sekcja „Źródła (0)"
                // byłaby gorsza niż nadmiar.
                if (filtruj && !cited[card.id]) { card.classList.add('print-skip'); skipped.push(card); }
                else kept++;
            });
            var sum = box.querySelector('summary');
            if (sum) { labels.push([sum, sum.textContent]); sum.textContent = 'Źródła (' + kept + ')'; }
        });
    }

    function revertPrint() {
        opened.forEach(function (d) { d.open = false; });
        skipped.forEach(function (c) { c.classList.remove('print-skip'); });
        labels.forEach(function (p) { p[0].textContent = p[1]; });
        opened = []; skipped = []; labels = [];
    }

    window.addEventListener('beforeprint', applyPrint);
    window.addEventListener('afterprint', revertPrint);

    return {
        // window.print() jest modalne i synchroniczne: wywołane w tym samym ticku trzymałoby otwarte
        // wywołanie interopu (a z nim dispatch obwodu Blazor Server) aż do zamknięcia okienka.
        print: function () { setTimeout(function () { window.print(); }, 0); },

        // true tylko przy realnym zapisie do schowka. navigator.clipboard NIE ISTNIEJE poza secure
        // context (np. http po IP w LAN), więc bez tego strażnika interop rzuciłby wyjątkiem zamiast
        // zwrócić false. Fallback (raport do ręcznego zaznaczenia) robi strona po stronie Blazora.
        copy: async function (text) {
            if (!window.isSecureContext || !navigator.clipboard) return false;
            try {
                await navigator.clipboard.writeText(text);
                return true;
            } catch (e) {
                return false;
            }
        }
    };
})();
