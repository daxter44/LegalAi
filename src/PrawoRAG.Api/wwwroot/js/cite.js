// Klikalne cytowania [n] w odpowiedzi czatu: kotwica #src-{anchorId}-{n} wskazuje kartę źródła.
// Delegacja na document (odpowiedzi renderują się dynamicznie ze streamingu — Blazor podmienia DOM);
// preventDefault, bo router Blazora przechwytuje nawigację wewnętrzną i fragment mógłby przeładować trasę.
document.addEventListener('click', function (e) {
    const a = e.target.closest('a.cite');
    if (!a) return;
    const id = (a.getAttribute('href') || '').slice(1);
    const card = document.getElementById(id);
    if (!card) return;
    e.preventDefault();

    // Panel „Źródła" bywa zwinięty — kotwica do środka zamkniętego <details> nic by nie pokazała.
    const details = card.closest('details');
    if (details) details.open = true;

    card.scrollIntoView({ behavior: 'smooth', block: 'center' });
    // Restart animacji podświetlenia przy ponownym kliknięciu tego samego źródła.
    card.classList.remove('cite-flash');
    void card.offsetWidth;
    card.classList.add('cite-flash');
});
