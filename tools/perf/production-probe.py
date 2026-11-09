#!/usr/bin/env python3
"""Run: python3 tools/perf/production-probe.py > tools/perf/results/2026-11-05-production.json

Read-only, one SSH connection: five docker-stats samples, SELECT-only aggregate
SQL, and logs reduced ON wizard to latency/token/cost aggregates. No personal
database rows or raw logs leave wizard. Avoids the app CLI (startup migrates).
"""
import pathlib
import subprocess

sql = pathlib.Path(__file__).with_name("production.sql").read_text()
worker = "SQL = " + repr(sql) + "\n" + r'''
import datetime,json,math,re,subprocess

def run(arguments, stdin=None):
    result=subprocess.run(arguments,input=stdin,capture_output=True,text=True,timeout=90)
    if result.returncode:
        raise RuntimeError("Read-only command failed: "+arguments[0]+": "+result.stderr.strip())
    return result.stdout.strip()

def json_stream(value):
    decoder=json.JSONDecoder(); records=[]
    while value.strip():
        value=value.lstrip(); record,consumed=decoder.raw_decode(value)
        records.append(record); value=value[consumed:]
    return records

def percentile(values,p):
    if not values:return None
    values=sorted(values); i=(len(values)-1)*p; a=int(i); b=min(a+1,len(values)-1)
    return values[a]+(values[b]-values[a])*(i-a)

collected={"captured_at_utc":datetime.datetime.now(datetime.timezone.utc).isoformat()}
collected["host"]={name:run(command) for name,command in [
    ("cores",["nproc"]),("memory",["free","-m"]),("disk",["df","-h"]),
    ("docker_disk",["docker","system","df"])]}
collected["containers"]=run(["docker","inspect","--format",
    '{{json .Name}} status={{.State.Status}} oom={{.State.OOMKilled}} started={{.State.StartedAt}} memory={{.HostConfig.Memory}} nanocpus={{.HostConfig.NanoCpus}} mounts={{json .Mounts}} restarts={{.RestartCount}} logging={{json .HostConfig.LogConfig}}',
    "plate-app","plate-db"])
collected["stats"]=[]
for _ in range(5):
    collected["stats"].append([json.loads(line) for line in run([
        "docker","stats","--no-stream","--format","{{json .}}","plate-app","plate-db"]).splitlines()])
psql='PGOPTIONS="-c default_transaction_read_only=on -c statement_timeout=30000" psql -X -v ON_ERROR_STOP=1 -A -t -U "$POSTGRES_USER" -d "$POSTGRES_DB"'
collected["database"]=json_stream(run(["docker","exec","-i","plate-db","sh","-c",psql],SQL))
samples={"food_namer":[],"jev":[]}
failures={"namer_timeout":0,"jev_timeout":0,"namer_failure":0,"jev_failure":0}
process=subprocess.Popen(["docker","logs","--since","720h","plate-app"],stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True)
for line in process.stdout:
    m=re.search(r"Food namer .* named (\d+) items in ([\d.]+) ms, (\d+) input and (\d+) output tokens, \$([\d.]+)",line)
    if m:samples["food_namer"].append([float(m[2]),int(m[3]),int(m[4]),float(m[5])])
    m=re.search(r"Jev .* decided (\d+) items in ([\d.]+) ms, (\d+) input tokens, \$([\d.]+)",line)
    if m:samples["jev"].append([float(m[2]),int(m[3]),0,float(m[4])])
    if "Food namer:" in line and "took longer" in line:failures["namer_timeout"]+=1
    if "Jev took longer" in line:failures["jev_timeout"]+=1
    if "Food namer request failed" in line:failures["namer_failure"]+=1
    if "Jev request failed" in line:failures["jev_failure"]+=1
if process.wait()!=0:raise RuntimeError("docker logs failed")
result={}
for name,rows in samples.items():
    times=[r[0] for r in rows]
    result[name]={"logged_successes":len(rows),"latency_ms_p50":percentile(times,.5),"latency_ms_p95":percentile(times,.95),
        "input_tokens":sum(r[1] for r in rows),"output_tokens":sum(r[2] for r in rows),"cost_usd":sum(r[3] for r in rows)}
collected["ai_logs"]={"window":"30d requested; actual coverage limited to current container log retention",
    "successes":result,"failures":failures}
print(json.dumps(collected,indent=2))
'''
result = subprocess.run(["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=20", "wizard", "python3 -"],
                        input=worker, text=True, timeout=180)
raise SystemExit(result.returncode)
