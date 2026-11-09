#!/usr/bin/env python3
"""Run: python3 tools/perf/public-probe.py > tools/perf/results/2026-11-05-public.json

Five sequential anonymous GETs per URL, no cookies or login. Saves only headers,
timings and the public build version, never response bodies. Uses curl --compressed.
"""
import datetime
import json
import pathlib
import re
import statistics
import subprocess
import tempfile

BASE = "https://plate.peychev.uk"
rows = []
for path in ["/account/login", "/sw.js", "/lib/chartjs/chart.umd.min.js"]:
    for run in range(1, 6):
        with tempfile.TemporaryDirectory(prefix="plate-public-") as directory:
            headers = pathlib.Path(directory, "headers")
            body = pathlib.Path(directory, "body")
            command = ["curl", "--silent", "--show-error", "--compressed", "--max-time", "30",
                       "--dump-header", str(headers), "--output", str(body),
                       "--write-out", "%{json}", BASE + path]
            result = subprocess.run(command, text=True, capture_output=True)
            row = {"path": path, "run": run, "exit_code": result.returncode}
            if result.returncode:
                row["error"] = result.stderr.strip()
            else:
                timing = json.loads(result.stdout)
                row.update({key: timing.get(key) for key in ["http_code", "http_version", "time_namelookup",
                           "time_connect", "time_appconnect", "time_starttransfer", "time_total", "size_download"]})
                parsed = {}
                for line in headers.read_text().splitlines():
                    if ":" in line:
                        key, value = line.split(":", 1)
                        if key.lower() in ["cache-control", "content-encoding", "content-type", "cf-cache-status",
                                           "server-timing", "etag", "age", "vary", "content-length"]:
                            parsed[key.lower()] = value.strip()
                row["headers"] = parsed
                if path == "/account/login":
                    match = re.search(r'data-version="([^"]+)"', body.read_text())
                    row["version"] = match.group(1) if match else None
            rows.append(row)
summary = []
for path in dict.fromkeys(row["path"] for row in rows):
    selected = [row for row in rows if row["path"] == path and row["exit_code"] == 0]
    if selected:
        values = [row["time_starttransfer"] * 1000 for row in selected]
        summary.append({"path": path, "n": len(values), "ttfb_ms_median": statistics.median(values),
                        "ttfb_ms_min": min(values), "ttfb_ms_max": max(values)})
print(json.dumps({"captured_at_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                  "environment": "Mac curl to production through public Cloudflare Tunnel; fresh connection per GET",
                  "summary": summary, "runs": rows}, indent=2))
raise SystemExit(1 if any(row["exit_code"] or row.get("http_code") != 200 for row in rows) else 0)
