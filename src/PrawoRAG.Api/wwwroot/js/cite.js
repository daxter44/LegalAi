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
