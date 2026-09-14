"""Summarize observe.ps1 CSVs without third-party packages or hardware access.

Usage: python summarize-observation.py path/to/observation [--output summary.json]
Counter changes are results, not errors. Invalid/missing readings are reported
separately and never silently converted into zero or discarded from the record.
"""

import argparse
import csv
import json
from collections import defaultdict
from datetime import datetime
from pathlib import Path


def timestamp(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def read_csv(path):
    # Windows PowerShell Export-Csv may emit a UTF-8 BOM; the C probe does not.
    with path.open(encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))
    if not rows:
        raise ValueError(f"No rows in {path}")
    return rows


def bounds(values):
    return {"min": min(values), "max": max(values)}


def reader_summary(rows):
    times = [timestamp(row["utc"]) for row in rows]
    valid = [row for row in rows if row["contexts_bytes"] == "4" and row["system_bytes"] == "4"]
    result = {
        "rows": len(rows),
        "valid_rows": len(valid),
        "invalid_rows": len(rows) - len(valid),
        "first_utc": rows[0]["utc"],
        "last_utc": rows[-1]["utc"],
        "duration_seconds": (times[-1] - times[0]).total_seconds(),
        "max_gap_seconds": max(((b - a).total_seconds() for a, b in zip(times, times[1:])), default=0),
        "distinct_pids": len({row["pid"] for row in rows}),
        "end_marker": rows[-1]["event"] == "sample_end",
    }
    if valid:
        for field in ("contexts", "system"):
            values = [int(row[field]) for row in valid]
            result[field] = bounds(values) | {"first": values[0], "last": values[-1]}
        result["changes"] = [
            {"utc": b["utc"], "from": int(a["contexts"]), "to": int(b["contexts"])}
            for a, b in zip(valid, valid[1:]) if a["contexts"] != b["contexts"]
        ]
    return result


def summarize(directory):
    readers = {name: read_csv(directory / f"{name}.csv") for name in ("persistent", "fresh")}
    metrics = defaultdict(list)
    for row in read_csv(directory / "process-metrics.csv"):
        metrics[row["name"]].append(row)
    process_results = {}
    for name, rows in sorted(metrics.items()):
        process_results[name] = {
            "samples": len(rows),
            "pids": sorted({int(row["pid"]) for row in rows}),
            "service_states": sorted({row["service_status"] for row in rows}),
        } | {
            field: bounds([int(row[field]) for row in rows])
            for field in ("private_bytes", "working_set_bytes", "handles", "threads")
        }
    return {
        "readers": {name: reader_summary(rows) for name, rows in readers.items()},
        "processes": process_results,
        "scope": "Sampled counters and process metrics only; no inference about unobserved intervals or driver internals.",
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    content = json.dumps(summarize(args.directory), indent=2) + "\n"
    if args.output:
        args.output.write_text(content, encoding="utf-8")
    else:
        print(content, end="")


if __name__ == "__main__":
    main()
