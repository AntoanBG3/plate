#!/usr/bin/env python3
# Usage: python3 tools/perf/summarize.py tools/perf/results/local-730d.json
import json,sys,statistics,math,collections
x=json.load(open(sys.argv[1]));groups=collections.defaultdict(list)
for s in x['samples']:
 if s['phase']=='warm':groups[(s['name'],s['c'])].append(s)
def pct(a,p):return sorted(a)[max(0,math.ceil(len(a)*p)-1)]
print('| Route | c | n | HTTP p50/p95 ms | app p50/p95 ms | DB p50 ms | query range | statuses |')
print('|---|---:|---:|---|---|---:|---|---|')
for (name,c),s in groups.items():
 vals=lambda k:[r[k] for r in s if k in r]
 print(f"| {name} | {c} | {len(s)} | {statistics.median(vals('ms')):.1f}/{pct(vals('ms'),.95):.1f} | {statistics.median(vals('app')):.1f}/{pct(vals('app'),.95):.1f} | {statistics.median(vals('db')):.1f} | {min(vals('queries'))}–{max(vals('queries'))} | {dict(collections.Counter(r['status'] for r in s))} |")
