#!/usr/bin/env python3
# Usage: python3 tools/perf/resources.py after-load > tools/perf/results/memory-after-load.json
# Five sequential samples from the isolated local containers, no workload generation.
import subprocess,json,sys,datetime
samples=[]
for i in range(5):
 s=subprocess.run(['docker','stats','--no-stream','--format','{{json .}}','repa-perf-app','repa-perf-db'],check=True,text=True,capture_output=True)
 samples.append({'run':i+1,'utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'containers':[json.loads(x)for x in s.stdout.splitlines()]})
print(json.dumps({'phase':sys.argv[1],'env':'native linux-arm64 app256MiB1CPU; db256MiB noCPUcap; synthetic5users730d','samples':samples},indent=2))
