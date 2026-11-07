#!/usr/bin/env python3
"""Usage: python3 tools/perf/search-compare.py baseline.json candidate.json

Recompute c1 percentiles and fail on a missing/failed sample or changed ordered food IDs.
"""
import json
import math
import statistics
import sys

def read(path):
    samples = json.load(open(path))["samples"]
    keyed = {(sample["name"], sample["batch"], sample["index"]): sample for sample in samples}
    assert len(keyed) == len(samples), "Duplicate sample keys"
    assert all(sample["c"] == 1 and sample["status"] == 200 for sample in samples), "Failed or non-c1 sample"
    assert all(sample["ids"] for sample in samples), "No IDs captured for a known matching fixture"
    return keyed

baseline, candidate = map(read, sys.argv[1:])
assert baseline.keys() == candidate.keys(), "Missing or extra samples"
assert all(baseline[key]["ids"] == candidate[key]["ids"] for key in baseline), "Changed ordered food IDs"
print(f"All {len(baseline)} paired responses have identical ordered food IDs.")
for name in dict.fromkeys(key[0] for key in baseline):
    groups = [[sample for key, sample in data.items() if key[0] == name] for data in (baseline, candidate)]
    assert all(len(group) == 50 for group in groups), "Expected five batches of ten samples"
    cells = []
    for group in groups:
        durations = sorted(sample["ms"] for sample in group)
        cells.append(f"{statistics.median(durations):.2f} / {durations[math.ceil(0.95 * len(durations)) - 1]:.2f} ms; EF commands {sorted(set(sample['queries'] for sample in group))}")
    print(name + ": " + " -> ".join(cells))
