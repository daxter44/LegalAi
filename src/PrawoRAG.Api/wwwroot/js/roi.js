// Kalkulator zwrotu z abonamentu na landingu (2026-09-08).
// Osobny plik, nie inline — CSP to `script-src 'self'`. Bez JS strona pokazuje wartości domyślne
// wpisane w HTML (progresywne wzbogacenie), więc sekcja czyta się także z wyłączonym skryptem.
(function () {
    'use strict';

    var PRICE = 149;        // cena early access, zł/mies. — zmiana = zmiana także w cenniku LandingHtml
    var SAVED_DOC = 55;     // analiza umowy: 60 min czytania -> 5 min w OmniaSI
    var SAVED_CASE = 30;    // research kazusu: 60 min w orzecznictwie -> 30 min

    var hour = document.getElementById('roi-hour');
    var docs = document.getElementById('roi-docs');
    var cases = document.getElementById('roi-cases');
    if (!hour || !docs || !cases) return;

    var outRate = document.getElementById('roi-rate');
    var outDocs = document.getElementById('roi-d');
    var outCases = document.getElementById('roi-k');
    var outNet = document.getElementById('roi-net');
    var outDetail = document.getElementById('roi-detail');
    var outBreak = document.getElementById('roi-break');

    function pl(value, digits) {
        return value.toLocaleString('pl-PL', { minimumFractionDigits: digits, maximumFractionDigits: digits });
    }

    function timeText(minutes) {
        var h = Math.floor(minutes / 60), m = Math.round(minutes % 60);
        if (h === 0) return m + ' min';
        return m === 0 ? h + ' godz.' : h + ' godz. ' + m + ' min';
    }

    function render() {
        var rate = parseInt(hour.value, 10);
        var d = parseInt(docs.value, 10);
        var k = parseInt(cases.value, 10);

        var minutes = d * SAVED_DOC + k * SAVED_CASE;
        var value = (minutes / 60) * rate;
        var net = value - PRICE;

        outRate.textContent = pl(rate, 0) + ' zł';
        outDocs.textContent = d;
        outCases.textContent = k;

        outNet.textContent = (net >= 0 ? '+' : '−') + pl(Math.abs(net), 0) + ' zł';

        outDetail.textContent = minutes === 0
            ? 'Przy zerowym użyciu zostaje sam koszt abonamentu — plan Start (0 zł) jest wtedy właściwym wyborem.'
            : 'Odzyskujesz ' + timeText(minutes) + ' miesięcznie — to ' + pl(value, 0) +
              ' zł Twojego czasu. Po odjęciu ' + PRICE + ' zł abonamentu ' +
              (net >= 0 ? 'zostaje ' + pl(net, 0) + ' zł na plus.' : 'brakuje ' + pl(-net, 0) + ' zł do zwrotu.');

        // Ile analiz dokumentów pokrywa abonament przy tej stawce.
        var perDoc = (SAVED_DOC / 60) * rate;
        outBreak.textContent = perDoc >= PRICE
            ? 'Przy tej stawce już jedna analiza umowy w miesiącu pokrywa cały abonament.'
            : 'Przy tej stawce abonament pokrywa ' + Math.ceil(PRICE / perDoc) + ' analizy umów w miesiącu.';
    }

    [hour, docs, cases].forEach(function (el) { el.addEventListener('input', render); });
    render();
})();
