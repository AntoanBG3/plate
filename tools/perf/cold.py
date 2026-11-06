#!/usr/bin/env python3
# Usage: python3 tools/perf/cold.py > tools/perf/results/cold.json
# Local synthetic app only; restarts its container five times. Never points at wizard.
import subprocess,ssl,urllib.request,time,json,os,tempfile
samples=[]
for run in range(1,6):
 subprocess.run(['docker','restart','repa-perf-app'],check=True,capture_output=True)
 deadline=time.monotonic()+60
 while True:
  try:
   if urllib.request.urlopen('https://localhost:5443/account/login',context=ssl._create_unverified_context(),timeout=1).status==200:break
  except Exception:
   if time.monotonic()>deadline:raise
   time.sleep(.05)
 with tempfile.NamedTemporaryFile(suffix='.json') as f:
  e=dict(os.environ,NODE_TLS_REJECT_UNAUTHORIZED='0')
  subprocess.run(['node','tools/perf/http.mjs','https://localhost:5443',f.name,'--cold'],env=e,check=True,capture_output=True)
  for row in json.load(open(f.name))['samples']:row['run']=run;samples.append(row)
print(json.dumps({'environment':'native linux-arm64 app256MiB1CPU; db256MiB noCPUcap; synthetic5users730d; app-process-cold, OS/PG buffers warm; login required before first signed-in page; routes in fixed order; not independent disk-cold pages','samples':samples},indent=2))
