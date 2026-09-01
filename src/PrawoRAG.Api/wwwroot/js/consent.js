// Baner zgody na cookies (ePrivacy/RODO) — self-hosted, bo CSP nie dopuszcza skryptów inline.
// Skrypty analityczne (Microsoft Clarity, Google Analytics) ładują się WYŁĄCZNIE po zgodzie
// "analityczne"; "tylko niezbędne" nie ładuje niczego. Wybór trwały (cookie 12 mies.),
// wycofywalny: każdy element o id="cookie-settings" otwiera baner ponownie.
(function () {
    'use strict';
    var COOKIE = 'omniasi-consent';
    var me = document.currentScript;
    if (!me) return;
    var clarityId = me.getAttribute('data-clarity') || '';
    var gaId = me.getAttribute('data-ga') || '';
    if (!clarityId && !gaId) return;

    function readChoice() {
        var m = document.cookie.match(new RegExp('(?:^|;\\s*)' + COOKIE + '=([^;]*)'));
        return m ? m[1] : null;
    }
    function saveChoice(v) {
        document.cookie = COOKIE + '=' + v + '; max-age=31536000; path=/; SameSite=Lax';
    }

    var loaded = false;
    function loadAnalytics() {
        if (loaded) return;
        loaded = true;
        if (clarityId) {
            // Odpowiednik oficjalnego snippetu Clarity, bez inline JS: kolejka + zewnętrzny tag.
            window.clarity = window.clarity || function () {
                (window.clarity.q = window.clarity.q || []).push(arguments);
            };
            var c = document.createElement('script');
            c.async = true;
            c.src = 'https://www.clarity.ms/tag/' + encodeURIComponent(clarityId);
            document.head.appendChild(c);
        }
        if (gaId) {
            window.dataLayer = window.dataLayer || [];
            function gtag() { window.dataLayer.push(arguments); }
            window.gtag = window.gtag || gtag;
            var g = document.createElement('script');
            g.async = true;
            g.src = 'https://www.googletagmanager.com/gtag/js?id=' + encodeURIComponent(gaId);
            document.head.appendChild(g);
            window.gtag('js', new Date());
            // IP i tak jest maskowane w GA4; wyłączamy sygnały reklamowe — to analityka, nie ads.
            window.gtag('config', gaId, { allow_google_signals: false, allow_ad_personalization_signals: false });
        }
    }

    var STYLE = '.omni-cc{position:fixed;left:0;right:0;bottom:0;z-index:10000;background:#171B24;' +
        'color:#EDEFF8;font:14px/1.55 Inter,-apple-system,"Segoe UI",sans-serif;padding:16px 5vw;' +
        'display:flex;gap:16px;align-items:center;flex-wrap:wrap;border-top:1px solid rgba(199,208,236,.18);' +
        'box-shadow:0 -10px 20px -4px rgba(0,0,0,.35)}' +
        '.omni-cc p{margin:0;flex:1 1 28rem;color:#C7D0EC}' +
        '.omni-cc a{color:#93B4FF}' +
        '.omni-cc .omni-cc-btns{display:flex;gap:10px;flex-wrap:wrap}' +
        '.omni-cc button{font:inherit;font-weight:700;min-height:42px;padding:0 18px;border-radius:12px;cursor:pointer}' +
        '.omni-cc .omni-cc-ok{border:0;color:#fff;background:linear-gradient(135deg,#2563EB 0%,#7C3AED 100%)}' +
        '.omni-cc .omni-cc-no{background:transparent;color:#EDEFF8;border:1px solid rgba(199,208,236,.35)}';

    function showBanner() {
        if (document.querySelector('.omni-cc')) return;
        var style = document.createElement('style');
        style.textContent = STYLE;
        document.head.appendChild(style);

        var box = document.createElement('div');
        box.className = 'omni-cc';
        box.setAttribute('role', 'dialog');
        box.setAttribute('aria-label', 'Zgoda na pliki cookie');

        var p = document.createElement('p');
        p.append('Poza niezbędnymi plikami cookie chcielibyśmy używać analitycznych (Microsoft Clarity, ' +
            'Google Analytics), żeby rozumieć, jak używany jest OmniaSI. Załadują się tylko za Twoją zgodą. ');
        var link = document.createElement('a');
        link.href = '/prywatnosc';
        link.textContent = 'Polityka prywatności';
        p.appendChild(link);
        p.append('.');

        var btns = document.createElement('div');
        btns.className = 'omni-cc-btns';
        var no = document.createElement('button');
        no.className = 'omni-cc-no';
        no.textContent = 'Tylko niezbędne';
        no.onclick = function () { saveChoice('necessary'); box.remove(); };
        var ok = document.createElement('button');
        ok.className = 'omni-cc-ok';
        ok.textContent = 'Zgadzam się na analityczne';
        ok.onclick = function () { saveChoice('analytics'); box.remove(); loadAnalytics(); };
        btns.append(no, ok);

        box.append(p, btns);
        document.body.appendChild(box);
    }

    function init() {
        var choice = readChoice();
        if (choice === 'analytics') loadAnalytics();
        else if (choice === null) showBanner();

        // Wycofanie/zmiana zgody: dowolny element o id="cookie-settings" otwiera baner ponownie.
        var settings = document.getElementById('cookie-settings');
        if (settings) settings.addEventListener('click', function (e) { e.preventDefault(); showBanner(); });
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
})();
