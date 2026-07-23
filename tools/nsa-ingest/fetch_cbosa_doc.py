#!/usr/bin/env python3
"""
CELOWANE pobranie POJEDYNCZYCH orzeczeń z CBOSA (orzeczenia.nsa.gov.pl/doc/<ID>).

Uzupełnia `fetch_nsa_wyroki.py` (backfill z datasetu JuDDGES) tam, gdzie datasetu NIE WYSTARCZA:
orzeczenia nowsze niż jego stan (raw: ~2025-03) albo brakujące mimo mieszczenia się w zakresie.
NIE jest crawlerem: pobiera WYŁĄCZNIE jawnie podane identyfikatory, po jednym, z przerwą.

Zgodność z ustaleniami z docs/ANALIZA-INGESTIA-CBOSA.md:
  - robots.txt CBOSA zabrania botom `/cbo/find|search|do/*`; strony `/doc/<ID>` NIE są zabronione;
  - jawny User-Agent, brak obchodzenia jakichkolwiek blokad (gdy serwer ograniczy — zwalniamy);
  - orzeczenia to materiały urzędowe (art. 4 pr. aut.); kwalifikacja formalna → bramka prawna.

Wynik: {RAW_ROOT}/NSA/{sanitized-id}.json w formacie StoredRawDocument — IDENTYCZNYM z tym, który
produkuje fetch_nsa_wyroki.py (pola SourcePayload nazwane jak w JuDDGES), więc czyta go ten sam
NsaNormalizer bez żadnych zmian. Idempotentny: pomija już zapisane (--force nadpisuje).

Użycie:
  python fetch_cbosa_doc.py --raw-root src/PrawoRAG.Ingestion/data/raw 74E9E040BB [ID...]
  python fetch_cbosa_doc.py --raw-root ... --url https://orzeczenia.nsa.gov.pl/doc/74E9E040BB

Potem embedowanie istniejącym pipeline'em:
  Ingestion__Source=NSA Ingestion__Mode=process dotnet run -c Release --project src/PrawoRAG.Ingestion
"""
import argparse
import hashlib
import html
import json
import re
import sys
import time
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

BASE_URL = "https://orzeczenia.nsa.gov.pl"
USER_AGENT = "PrawoRAG/0.1 (research prawniczy; pojedyncze dokumenty, nie crawl)"
RESERVED = set('/\\:*?"<>|')

# Etykiety CBOSA → pola SourcePayload w nazewnictwie JuDDGES (tak je czyta NsaNormalizer).
LABEL_MAP = {
    "Sąd": "court_name",
    "Sędziowie": "judges",
    "Hasła tematyczne": "keywords",
    "Symbol z opisem": "case_type_description",
    "Skarżony organ": "challenged_authority",
    "Treść wyniku": "decision",
}
SINGLE_VALUE = {"court_name", "challenged_authority"}


def sanitize(external_id: str) -> str:
    return "".join("_" if (c in RESERVED or ord(c) < 32) else c for c in external_id)


def strip_tags(fragment: str) -> str:
    """HTML → tekst: <br> i </p> jako podział linii, reszta znaczników usunięta, encje odkodowane."""
    s = re.sub(r"<\s*br\s*/?>", "\n", fragment, flags=re.I)
    s = re.sub(r"</\s*(p|div|tr)\s*>", "\n", s, flags=re.I)
    s = re.sub(r"<[^>]+>", "", s)
    s = html.unescape(s).replace("\xa0", " ")
    return "\n".join(line.strip() for line in s.split("\n")).strip()


def lines_of(fragment: str) -> list:
    return [ln for ln in strip_tags(fragment).split("\n") if ln]


def parse_rows(page: str) -> list:
    """Wiersze tabeli metadanych: (etykieta, surowy HTML wartości). Obsługuje oba warianty klas
    (zwykły `info-list-*` i `-uzasadnienie` dla sentencji/uzasadnienia).

    Granicą wartości jest POCZĄTEK KOLEJNEGO WIERSZA, nie najbliższy `</td>`/`</span>`: komórka
    wartości bywa ZAGNIEŻDŻONĄ tabelą (data orzeczenia i prawomocność siedzą w osobnych <td>),
    a „Powołane przepisy" zawierają serię <span class='nakt'>. Dopasowanie do pierwszego domknięcia
    ucinało wszystko poza pierwszym elementem — gubiło prawomocność i wszystkie przepisy prócz
    pierwszego. Stopka odcinana z góry, żeby ostatni wiersz nie wciągnął nawigacji i disclaimera."""
    footer = page.find('<div class="dolne-linki"')
    if footer > 0:
        page = page[:footer]

    rows = []
    for chunk in re.split(r'<tr class="niezaznaczona">', page)[1:]:
        label_m = re.search(r'lista-label">(?P<label>.*?)</(?:td|div)>', chunk, re.S)
        value_m = re.search(
            r'<(?:td|span) class="info-list-value(?:-uzasadnienie)?"[^>]*>(?P<value>.*)', chunk, re.S)
        if label_m and value_m:
            rows.append((strip_tags(label_m.group("label")), value_m.group("value")))
    return rows


def parse_legal_bases(fragment: str) -> list:
    """„Powołane przepisy" → lista obiektów {link, article, journal, law} — kształt jak w JuDDGES.
    Wzorzec CBOSA: <a href=ISAP>Dz.U. …</a> art. …<br/><span class='nakt'>Ustawa …</span><br/>"""
    out = []
    pattern = re.compile(
        r'<a href="(?P<link>[^"]+)"[^>]*>(?P<journal>.*?)</a>(?P<article>.*?)'
        r"<br\s*/?>\s*<span class='nakt'>(?P<law>.*?)</span>",
        re.S | re.I,
    )
    for m in pattern.finditer(fragment):
        out.append({
            "link": html.unescape(m.group("link")),
            "article": strip_tags(m.group("article")),
            "journal": strip_tags(m.group("journal")),
            "law": re.sub(r"\s{2,}", " ", strip_tags(m.group("law"))),
        })
    return out


def parse_doc(page: str, doc_id: str) -> dict:
    """Strona /doc/<ID> → StoredRawDocument. Rzuca ValueError, gdy brak treści (nie zapisujemy widma)."""
    payload = {}

    # Nagłówek „IV SA/Po 655/24 - Wyrok WSA w Poznaniu" → sygnatura + typ orzeczenia.
    head = re.search(r'<span class="war_header">(.*?)</span>', page, re.S)
    if not head:
        head = re.search(r"<title>(.*?)</title>", page, re.S)
    heading = strip_tags(head.group(1)) if head else ""
    heading = re.sub(r"\s+z\s+\d{4}-\d{2}-\d{2}\s*$", "", heading)  # tytuł strony ma jeszcze datę
    if " - " in heading:
        docket, jtype = heading.split(" - ", 1)
        payload["docket_number"] = docket.strip()
        payload["judgment_type"] = jtype.strip()
    elif heading:
        payload["docket_number"] = heading

    sentencja = uzasadnienie = ""
    for label, value_html in parse_rows(page):
        if label == "Data orzeczenia":
            # Ta komórka niesie datę ORAZ prawomocność (osobne linie).
            parts = lines_of(value_html)
            if parts:
                payload["judgment_date"] = parts[0]
            for p in parts[1:]:
                if "prawomocn" in p.lower():
                    payload["finality"] = p
        elif label == "Powołane przepisy":
            bases = parse_legal_bases(value_html)
            if bases:
                payload["extracted_legal_bases"] = bases
        elif label == "Sentencja":
            sentencja = strip_tags(value_html)
        elif label == "Uzasadnienie":
            uzasadnienie = strip_tags(value_html)
        elif label == "Tezy":
            payload["thesis"] = strip_tags(value_html)
        elif label in LABEL_MAP:
            field = LABEL_MAP[label]
            values = lines_of(value_html)
            if field == "judges":
                # CBOSA dopisuje role: „Maciej Busz /przewodniczący/" — JuDDGES trzyma same nazwiska.
                values = [re.sub(r"\s*/[^/]*/\s*", "", v).strip() for v in values]
                values = [v for v in values if v]
            if not values:
                continue
            payload[field] = values[0] if field in SINGLE_VALUE else values

    if not sentencja and not uzasadnienie:
        raise ValueError("brak sentencji i uzasadnienia (strona bez treści orzeczenia?)")

    # Układ full_text IDENTYCZNY z JuDDGES — NsaNormalizer dzieli sekcje po literalnym „UZASADNIENIE".
    full_text = "SENTENCJA\n\n" + sentencja
    if uzasadnienie:
        full_text += "\n\n\nUZASADNIENIE\n\n" + uzasadnienie

    external_id = f"/doc/{doc_id}"
    return {
        "SchemaVersion": 1,
        "Source": "NSA",
        "ExternalId": external_id,
        "DocType": "nsa-judgment",
        "RawContent": full_text,
        "ContentFormat": "plain-text",
        "SourceUrl": BASE_URL + external_id,
        "SourceModificationDate": payload.get("judgment_date"),
        "SourcePayload": payload,
        "FetchedAt": datetime.now(timezone.utc).isoformat(),
        "ContentHash": hashlib.sha256(full_text.encode("utf-8")).hexdigest(),
    }


def fetch(doc_id: str, timeout: int, attempts: int = 3) -> str:
    """Pobranie strony z ponawianiem: serwer CBOSA potrafi zerwać połączenie bez odpowiedzi
    (zaobserwowane), a kolejna próba przechodzi. To odporność na chwilowe awarie, NIE obchodzenie
    blokad — przy odpowiedzi odmownej (4xx/5xx) urllib rzuca HTTPError i kończymy bez ponawiania."""
    headers = {
        "User-Agent": USER_AGENT,
        "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
        "Accept-Language": "pl-PL,pl;q=0.9",
    }
    req = urllib.request.Request(f"{BASE_URL}/doc/{doc_id}", headers=headers)
    for attempt in range(1, attempts + 1):
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                charset = resp.headers.get_content_charset() or "utf-8"
                return resp.read().decode(charset, errors="replace")
        except urllib.error.HTTPError:
            raise  # jawna odmowa serwera — nie naciskamy
        except Exception:
            if attempt == attempts:
                raise
            time.sleep(2.0 * attempt)  # narastająca przerwa
    raise RuntimeError("nieosiągalne")


def main() -> int:
    ap = argparse.ArgumentParser(description="Celowane pobranie pojedynczych orzeczeń z CBOSA.")
    ap.add_argument("ids", nargs="*", help="Identyfikatory dokumentów, np. 74E9E040BB")
    ap.add_argument("--url", action="append", default=[], help="Pełny URL /doc/<ID> (zamiast samego ID)")
    ap.add_argument("--raw-root", required=True, help="Katalog magazynu surowych (RawStore:RootPath)")
    ap.add_argument("--force", action="store_true", help="Nadpisz, jeśli plik już istnieje")
    ap.add_argument("--delay", type=float, default=2.0, help="Przerwa między żądaniami [s]")
    ap.add_argument("--timeout", type=int, default=30)
    args = ap.parse_args()

    ids = list(args.ids)
    for u in args.url:
        m = re.search(r"/doc/([A-Za-z0-9]+)", u)
        if not m:
            print(f"Nie rozpoznaję identyfikatora w URL: {u}", file=sys.stderr)
            return 2
        ids.append(m.group(1))
    if not ids:
        print("Podaj co najmniej jeden identyfikator (albo --url).", file=sys.stderr)
        return 2

    out_dir = Path(args.raw_root) / "NSA"
    out_dir.mkdir(parents=True, exist_ok=True)

    written = skipped = failed = 0
    for i, doc_id in enumerate(ids):
        path = out_dir / (sanitize(f"/doc/{doc_id}") + ".json")
        if path.exists() and not args.force:
            print(f"POMIJAM (jest już w magazynie): {doc_id} → {path}")
            skipped += 1
            continue
        if i > 0:
            time.sleep(args.delay)  # uczciwe tempo wobec serwera sądu
        try:
            stored = parse_doc(fetch(doc_id, args.timeout), doc_id)
        except Exception as ex:  # noqa: BLE001 — komunikat ma być czytelny, nie stacktrace
            print(f"BŁĄD {doc_id}: {ex}", file=sys.stderr)
            failed += 1
            continue

        tmp = path.with_suffix(".json.tmp")
        tmp.write_text(json.dumps(stored, ensure_ascii=False, indent=2), encoding="utf-8")
        tmp.replace(path)  # atomowa publikacja (jak FileSystemRawDocumentStore)
        written += 1
        p = stored["SourcePayload"]
        print(f"ZAPISANO {doc_id}: {p.get('docket_number')} — {p.get('judgment_type')} "
              f"({p.get('judgment_date')}), {len(stored['RawContent'])} znaków → {path}")

    print(f"\nGOTOWE: zapisano={written} pominięto={skipped} błędy={failed}")
    if written:
        print("Teraz embeduj: Ingestion__Source=NSA Ingestion__Mode=process "
              "dotnet run -c Release --project src/PrawoRAG.Ingestion")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
