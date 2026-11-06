#!/usr/bin/env python3
# Usage: python3 tools/perf/checks.py --runs 5
# Sequential full-suite duration samples. No timing benchmark may run alongside this.
import argparse,subprocess,time,json,pathlib,re,datetime,sys
p=argparse.ArgumentParser();p.add_argument('--runs',type=int,default=5);a=p.parse_args()
assert 1<=a.runs<=10
output=pathlib.Path('tools/perf/results/checks.json')
data=json.loads(output.read_text()) if output.exists() else {'command':'bash tools/check.sh','environment':'Mac16,13 arm64 .NET10.0.107 Node26.11.1 DockerDesktop10CPUs7.75GiB; warm package/image/browser caches','samples':[]}
for _ in range(a.runs):
 n=len(data['samples'])+1;log=pathlib.Path(f'/private/tmp/repa-perf-check-{n}.log')
 with log.open('w') as f:
  start=time.monotonic();result=subprocess.run(['bash','tools/check.sh'],stdout=f,stderr=subprocess.STDOUT);duration=time.monotonic()-start
 text=log.read_text()
 dotnet=[{'project':m[2],'passed':int(m[1])}for m in re.finditer(r'Passed!\s+- Failed:\s+0, Passed:\s+(\d+),.*?- (GymTracker\.\w+\.Tests)\.dll',text)]
 row={'run':n,'utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'exit':result.returncode,'seconds':duration,'jsPassed':int(re.search(r'pass (\d+)',text)[1]) if re.search(r'pass (\d+)',text)else None,'dotnet':dotnet}
 data['samples'].append(row);output.write_text(json.dumps(data,indent=2));print(json.dumps(row),flush=True)
 if result.returncode:sys.exit(result.returncode)
