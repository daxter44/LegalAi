#!/usr/bin/env python3
"""
Backfill orzeczeń NSA/WSA do magazynu surowych PrawoRAG — TYLKO WYROKI.

Źródło: dataset JuDDGES/pl-nsa (Hugging Face, CC BY 4.0) — pochodzi z CBOSA, ma pełne teksty
(`full_text`) i metadane. Filtr `judgment_type LIKE 'Wyrok%'` odsiewa postanowienia proceduralne
(dominują liczebnie, znikoma wartość) — patrz docs/ANALIZA-INGESTIA-CBOSA.md. Efekt: ~650 tys.
wyroków zamiast 2,39 mln, co MIEŚCI SIĘ na 3060 bez kryzysu indeksu.

Wynik: jeden plik JSON per wyrok w {RAW_ROOT}/NSA/{sanitized-id}.json w formacie StoredRawDocument
(zgodnym z FileSystemRawDocumentStore) — potem `Ingestion:Mode=process Ingestion:Source=NSA` embeduje
je istniejącym pipeline'em (RÓWN-1/2/3, idempotencja, reprocess-failed). Skrypt jest IDEMPOTENTNY:
pomija pliki już zapisane, więc można wznawiać po przerwaniu.

Wymaga: pip install datasets pyarrow. Streaming (nie ładuje 31 GB do RAM).

Użycie:
  python fetch_nsa_wyroki.py --raw-root /sciezka/do/magazynu [--since 2016-01-01] [--limit N] [--dataset JuDDGES/pl-nsa]

Domyślnie POMIJA wyroki bez sekcji UZASADNIENIE (sama sentencja — wyrok nieprawomocny, uzasadnienie
dojdzie później; 20% zbioru). --keep-without-justification wyłącza ten filtr.

Uwaga suwerenności: to jednorazowe pobranie PUBLICZNYCH danych (orzeczenia to materiały urzędowe);
runtime produktu pozostaje PL/UE. Kwestie prawne (CC BY, nota CBOSA) → bramka 0.5 u prawnika.
"""
import argparse
import hashlib
import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

RESERVED = set('/\\:*?"<>|')
BASE_URL = "https://orzeczenia.nsa.gov.pl"

# Pola metadanych przenoszone do SourcePayload (czyta je NsaNormalizer). Pełny full_text idzie do
# RawContent, nie do payloadu (żeby nie dublować treści).
META_FIELDS = [
    "docket_number", "court_name", "judgment_type", "finality", "judgment_date",
    "judges", "keywords", "case_type_description", "challenged_authority",
    "extracted_legal_bases", "decision", "thesis",
]


def sanitize(external_id: str) -> str:
    return "".join("_" if (c in RESERVED or ord(c) < 32) else c for c in external_id)


def to_jsonable(v):
    # pyarrow/datasets zwraca m.in. numpy/datetime — sprowadzamy do czystego JSON.
    if v is None:
        return None
    if isinstance(v, (str, int, float, bool)):
        return v
    if isinstance(v, datetime):
        return v.isoformat()
    if isinstance(v, dict):
        return {k: to_jsonable(x) for k, x in v.items()}
    if isinstance(v, (list, tuple)) or hasattr(v, "__iter__"):
        return [to_jsonable(x) for x in v]
    return str(v)


def judgment_on_or_after(judgment_date, since) -> bool:
    """Data orzeczenia (datetime / date / ISO-string z offsetem, np. '2005-10-13T00:00:00+02:00') >= since.
    Brak lub nieczytelna data → False (wyrok bez daty nie przechodzi filtra zakresu)."""
    if judgment_date is None:
        return False
    if isinstance(judgment_date, datetime):
        return judgment_date.date() >= since
    if hasattr(judgment_date, "year") and not isinstance(judgment_date, str):
        return judgment_date >= since
    try:
        return datetime.fromisoformat(str(judgment_date)[:19]).date() >= since
    except ValueError:
        return False


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--raw-root", required=True, help="Katalog magazynu surowych (RawStore:RootPath)")
    ap.add_argument("--dataset", default="JuDDGES/pl-nsa")
    ap.add_argument("--limit", type=int, default=None, help="Maks. liczba WYROKÓW (smoke)")
    ap.add_argument("--since", default=None,
                    help="Tylko wyroki z datą orzeczenia >= YYYY-MM-DD (np. 2016-01-01); bez flagi: wszystkie lata")
    ap.add_argument("--keep-without-justification", action="store_true",
                    help="Zapisuj też wyroki bez sekcji UZASADNIENIE (domyślnie pomijane: sama sentencja nic nie wnosi)")
    ap.add_argument("--report-every", type=int, default=5000)
    args = ap.parse_args()
    since = datetime.fromisoformat(args.since).date() if args.since else None

    try:
        from datasets import load_dataset
    except ImportError:
        print("Brak pakietu 'datasets'. Zainstaluj: pip install datasets pyarrow", file=sys.stderr)
        return 2

    out_dir = Path(args.raw_root) / "NSA"
    out_dir.mkdir(parents=True, exist_ok=True)

    print(f"Streaming {args.dataset} → {out_dir} (tylko wyroki)")
    ds = load_dataset(args.dataset, split="train", streaming=True)

    seen = written = skipped_type = skipped_empty = skipped_exists = skipped_date = skipped_nojust = 0
    for row in ds:
        seen += 1
        jtype = (row.get("judgment_type") or "")
        if not jtype.startswith("Wyrok"):          # FILTR: tylko wyroki (odsiew postanowień/uchwał)
            skipped_type += 1
        elif since is not None and not judgment_on_or_after(row.get("judgment_date"), since):
            skipped_date += 1                        # FILTR: zakres dat (--since)
        else:
            external_id = row.get("judgment_id") or row.get("docket_number")
            full_text = (row.get("full_text") or "").strip()
            if not external_id or not full_text:    # dokument-widmo (sama sentencja bez treści) — pomijamy
                skipped_empty += 1
            elif not args.keep_without_justification and "UZASADNIENIE" not in full_text:
                # Sama sentencja (wyrok nieprawomocny, uzasadnienie dojdzie później) — zmierzone 2026-09-07:
                # 20% wyroków w korpusie, w latach 2020-2024 aż 26%; nie odpowie na żadne pytanie prawne.
                skipped_nojust += 1
            else:
                path = out_dir / (sanitize(external_id) + ".json")
                if path.exists():
                    skipped_exists += 1               # idempotencja — wznawianie po przerwaniu
                else:
                    content_hash = hashlib.sha256(full_text.encode("utf-8")).hexdigest()
                    payload = {k: to_jsonable(row.get(k)) for k in META_FIELDS if row.get(k) is not None}
                    stored = {
                        "SchemaVersion": 1,
                        "Source": "NSA",
                        "ExternalId": external_id,
                        "DocType": "nsa-judgment",
                        "RawContent": full_text,
                        "ContentFormat": "plain-text",
                        "SourceUrl": BASE_URL + external_id if external_id.startswith("/") else None,
                        "SourceModificationDate": to_jsonable(row.get("judgment_date")),
                        "SourcePayload": payload,
                        "FetchedAt": datetime.now(timezone.utc).isoformat(),
                        "ContentHash": content_hash,
                    }
                    tmp = path.with_suffix(".json.tmp")
                    tmp.write_text(json.dumps(stored, ensure_ascii=False, indent=2), encoding="utf-8")
                    tmp.replace(path)                 # atomowa publikacja (jak FileSystemRawDocumentStore)
                    written += 1
                    if args.limit and written >= args.limit:
                        print("Osiągnięto --limit.")
                        break
        if seen % args.report_every == 0:
            print(f"  seen={seen} written={written} skip(type)={skipped_type} "
                  f"skip(empty)={skipped_empty} skip(exists)={skipped_exists} skip(date)={skipped_date} skip(bez-uzasadnienia)={skipped_nojust}")

    print(f"GOTOWE: seen={seen} written={written} skip(type)={skipped_type} "
          f"skip(empty)={skipped_empty} skip(exists)={skipped_exists} skip(date)={skipped_date} skip(bez-uzasadnienia)={skipped_nojust}")
    print(f"Teraz embeduj: Ingestion__Source=NSA Ingestion__Mode=process "
          f"Ingestion__ProcessParallelism=8 dotnet run --project src/PrawoRAG.Ingestion")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
