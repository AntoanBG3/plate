#!/usr/bin/env python3
# Usage: python3 tools/perf/startup.py > tools/perf/results/startup.json
# Local only; creates five fresh synthetic databases, then measures five existing-database starts.
import subprocess,time,json,urllib.request,ssl,uuid
image='mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:00e0ad6a7ef8c0c1391b87f05c7ac757a15740455688f2bfcd146a3f4b987efd'
def run(*args): return subprocess.run(args,check=True,capture_output=True,text=True).stdout
samples=[]
run_id=uuid.uuid4().hex[:8]
for phase in ['fresh-migrations-seed','existing-schema-seed-check']:
 for i in range(5):
  db=f'gym_perf_start_{run_id}_{i}'
  if phase.startswith('fresh'):run('docker','exec','repa-perf-db','psql','-U','gym','-d','postgres','-c',f'CREATE DATABASE {db}')
  args=['docker','run','-d','--name','repa-perf-start','--entrypoint','dotnet','--memory','256m','--cpus','1','-p','127.0.0.1:5444:8443','--add-host','host.docker.internal:host-gateway','-v','/private/tmp/repa-perf-publish:/app:ro','-v','/private/tmp/repa-perf-tls:/tls:ro','-e','ASPNETCORE_ENVIRONMENT=Production','-e','ASPNETCORE_URLS=https://+:8443','-e','ASPNETCORE_Kestrel__Certificates__Default__Path=/tls/cert.pfx','-e','ASPNETCORE_Kestrel__Certificates__Default__Password=perf-local-only','-e',f'ConnectionStrings__Default=Host=host.docker.internal;Port=55432;Database={db};Username=gym;Password=perf-local-only;Maximum Pool Size=8','-e','Logging__LogLevel__Default=Warning','-w','/app',image,'GymTracker.Web.dll']
  started=time.monotonic();run(*args)
  while True:
   try:
    r=urllib.request.urlopen('https://localhost:5444/account/login',context=ssl._create_unverified_context(),timeout=2)
    if r.status==200:break
   except Exception:
    if time.monotonic()-started>90:raise
    time.sleep(.05)
  ready=time.monotonic()-started
  info=json.loads(run('docker','inspect','repa-perf-start'))[0]
  samples.append({'phase':phase,'run':i+1,'readySeconds':ready,'state':{'oom':info['State']['OOMKilled'],'restarts':info['RestartCount']}})
  run('docker','rm','-f','repa-perf-start')
print(json.dumps({'commit':'462ef11','env':'native linux-arm64 Release app256MiB1CPU DB256MiB noCPUcap; from docker run through first successful GET; 50ms polling; fresh schema includes all28migrations+11795food seed; warm OS/runtime layers','samples':samples},indent=2))
