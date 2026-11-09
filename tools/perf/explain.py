#!/usr/bin/env python3
# Usage: python3 tools/perf/explain.py > tools/perf/results/explain.json
# Runs the source-based representative EXPLAIN script five times, sequentially, against LOCAL synthetic data only.
import json,subprocess,pathlib
sql=pathlib.Path(__file__).with_suffix('.sql').read_text();out=[]
for run in range(1,6):
 p=subprocess.run(['docker','exec','-i','plate-perf-db','psql','-X','-A','-t','-U','gym','-d','gym_perf','-v','email=demo@example.test'],input=sql,capture_output=True,text=True,check=True)
 s=p.stdout;dec=json.JSONDecoder();section=None
 while s.strip():
  s=s.lstrip()
  if s.startswith('['):
   plan,n=dec.raw_decode(s);s=s[n:];root=plan[0]
   def nodes(x):
    return [{k:x[k] for k in ['Node Type','Relation Name','Index Name','Actual Rows','Rows Removed by Filter','Shared Hit Blocks','Shared Read Blocks'] if k in x}]+[z for child in x.get('Plans',[]) for z in nodes(child)]
   out.append({'run':run,'section':section,'planningMs':root['Planning Time'],'executionMs':root['Execution Time'],'nodes':nodes(root['Plan'])})
  else:
   line,_,s=s.partition('\n')
   if line in ['food-search-original','session-recent-ids','weight-all-history','progress-target-snapshots-all-history','food-last-amount']:section=line
print(json.dumps({'environment':'native postgres18;256MiB,noCPUcap;synthetic5users730days; app idle, no concurrent timing workload; source-shaped SQL, not exact EF','firstRun':'first measured pass, OS/PG buffers previously warm from HTTP; not a disk-cold claim','samples':out},indent=2))
