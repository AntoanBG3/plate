#!/usr/bin/env python3
# Usage: python3 tools/perf/set-numbering.py tools/perf/results/local-730d.json > tools/perf/results/set-numbering-load.json
# Run immediately AFTER the HTTP benchmark and BEFORE reseeding. Local synthetic container only.
import json,sys,subprocess
measurement=json.load(open(sys.argv[1]))
exercise_id=int(measurement['seid'])
query=f'''SELECT json_build_object(
 'synthetic_only',true,'sets',count(*),'distinct_client_ids',count(DISTINCT "ClientId"),
 'distinct_numbers',count(DISTINCT "SetNumber"),'number_groups_with_duplicates',
 (SELECT count(*) FROM (SELECT "SetNumber" FROM "Sets" WHERE "SessionExerciseId"={exercise_id}
 GROUP BY "SetNumber" HAVING count(*)>1)d))
 FROM "Sets" WHERE "SessionExerciseId"={exercise_id};'''
result=subprocess.run(['docker','exec','repa-perf-db','psql','-U','gym','-d','gym_perf','-Atc',query],
                      check=True,capture_output=True,text=True)
value=json.loads(result.stdout)
if value['sets']==0:
 raise SystemExit('No measured sets remain; run the HTTP benchmark before this probe, without reseeding.')
print(json.dumps(value,indent=2))
