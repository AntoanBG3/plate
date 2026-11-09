#!/usr/bin/env python3
"""Usage: python3 tools/perf/search-explain.py > tools/perf/results/p7-explain.json

Requires the audit branch's isolated plate-perf-db synthetic stack. No production connection.
Five sequential baseline/candidate EXPLAIN pairs per case; run without tests/builds/load.
SQL bodies come from main/current FoodItemService, with psql literals replacing EF parameters.
"""
import hashlib
import json
import pathlib
import re
import subprocess

repo = pathlib.Path(__file__).resolve().parents[2]
path = "src/Plate.Web/Features/Food/FoodItemService.cs"
sources = {
    "baseline": subprocess.check_output(["git", "show", "main:" + path], cwd=repo, text=True),
    "candidate": (repo / path).read_text(),
}
templates = {key: re.findall(r'SqlQuery<FoodMatch>\(\$"""(.*?)"""', value, re.S)[:2] for key, value in sources.items()}
assert all(len(value) == 2 for value in templates.values())
cases = [("picker", query, 0.3) for query in ("chicken", "banana", "ориз", "кисело мляко", "chikcen")]
cases += [("matcher", "chicken", 0.3)] + [("matcher", query, 0.5) for query in ("chicken", "rice", "banana")]

def nodes(plan):
    fields = ("Node Type", "Relation Name", "Index Name", "Actual Rows", "Rows Removed by Filter", "Rows Removed by Index Recheck", "Shared Hit Blocks", "Shared Read Blocks")
    return [{key: plan[key] for key in fields if key in plan}] + [item for child in plan.get("Plans", []) for item in nodes(child)]

samples = []
for kind, query, threshold in cases:
    for run in range(1, 6):
        for label in sources:
            sql = templates[label][0 if kind == "matcher" else 1]
            bindings = {
                "q": ":'query'::text", "lower": "lower(:'query'::text)", "userId": ":'uid'::text",
                "minScore": str(threshold) + "::float8", "take": "50", "take * 2": "40", "indexed": "true",
            }
            for key, value in bindings.items():
                sql = sql.replace("{" + key + "}", value)
            assert not re.search(r"\{[^}]+\}", sql)
            script = '''SELECT "Id" AS uid FROM "AspNetUsers" WHERE "Email" = 'demo@example.test' \\gset
BEGIN;
SET LOCAL pg_trgm.word_similarity_threshold = ''' + ("0.29999998" if threshold == 0.3 else "0.49999997") + ";\nEXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + sql + ";\nROLLBACK;\n"
            result = subprocess.run([
                "docker", "exec", "-i", "plate-perf-db", "psql", "-X", "-qAt", "-U", "gym", "-d", "gym_perf",
                "-v", "ON_ERROR_STOP=1", "-v", "query=" + query,
            ], input=script, capture_output=True, text=True, check=True)
            plan = json.loads(result.stdout)[0]
            samples.append({"label": label, "kind": kind, "query": query, "threshold": threshold, "run": run,
                            "planningMs": plan["Planning Time"], "executionMs": plan["Execution Time"], "nodes": nodes(plan["Plan"])})
print(json.dumps({
    "environment": "PG18; 256 MiB/no CPU cap; 11,795 seeded foods; 5 synthetic users/730 days; warm buffers; app idle; no concurrent workload",
    "method": "source SQL bodies with typed literals, not byte-for-byte captured EF commands; normal planner settings; five sequential alternating pairs per case",
    "sourceSha256": {key: hashlib.sha256(value.encode()).hexdigest() for key, value in sources.items()},
    "samples": samples,
}, indent=2, ensure_ascii=False))
