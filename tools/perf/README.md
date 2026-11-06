# Reproducing the 2026-11-05 audit

Read [the audit](../../docs/perf/2026-11-05-audit.md) for environment,limits,samples and interpretation. Source baseline462ef11;no fixes included. Every measurement program documents its one-command invocation at the top.

Use native arm64 on this Mac. The production Dockerfile publishes x64;do not time it under emulation. Start Docker Desktop,then:

    bash tools/perf/setup.sh 730

This publishes existing sources,creates only local audit containers with bind mounts in /private/tmp,runs migrations/seeding,creates a disposable synthetic account,generates data and serves HTTPS at localhost:5443. Existing audit containers are refused. Generator date is anchored to2026-11-05;on another date update shared audit-date literals in generator/HTTP harness and record the change. No real OpenRouter key is configured.

Change dataset size without touching production/development databases:

    python3 tools/perf/generate.py --days 30 > /private/tmp/repa-perf-seed.sql
    docker exec -i repa-perf-db psql -U gym -d gym_perf -v ON_ERROR_STOP=1 < /private/tmp/repa-perf-seed.sql

Generator refuses a database name other than gym_perf. Synthetic journey rows are replaced transactionally;shared seeded foods remain. Use --days730 for two years. CLI account creation is only for a fresh scratch database;reuse an initialized stack instead of running setup on its old bind mount.

Run timing commands separately,waiting for each to finish:

    NODE_TLS_REJECT_UNAUTHORIZED=0 node tools/perf/http.mjs https://localhost:5443 tools/perf/results/local-730d.json
    NODE_TLS_REJECT_UNAUTHORIZED=0 node tools/perf/http.mjs https://localhost:5443 tools/perf/results/ramp.json --ramp
    python3 tools/perf/explain.py > tools/perf/results/explain.json
    python3 tools/perf/startup.py > tools/perf/results/startup.json
    python3 tools/perf/cold.py > tools/perf/results/cold.json
    python3 tools/perf/summarize.py tools/perf/results/local-730d.json

HTTP defaults:five batches per c1/c5,10requests/batch;sets5/batch. Ramp c1/5/10/20/40 on search. cold.py orchestrates five local app restarts. TLS verification is disabled only for the disposable loopback certificate;HTTP script rejects remote hosts. Never point load tools at wizard.

Capture numbering immediately after the HTTP run,before reseeding:\n\n    python3 tools/perf/set-numbering.py tools/perf/results/local-730d.json > tools/perf/results/set-numbering-load.json\n\nHTTP benchmarking adds sets/food rows. **Reseed730 days before cold/browser measurement** to measure an ordinary eight-entry day. No seeding,builds,GC diagnostics or tests alongside timing.

Browser development tool,pinned to measured version:

    npm install --cache /private/tmp/repa-perf-npm-cache --prefix /private/tmp/repa-perf-node playwright-core@1.64.1

Link design-samples/tools/node_modules to /private/tmp/repa-perf-node/node_modules, replacing only the existing dangling symlink. Then:

    node design-samples/tools/perf-audit.mjs https://localhost:5443 > tools/perf/results/browser.json

Installed Chrome;five empty-cache contexts per100/400ms proxy delay,390x844,4xCPU,shared1.5MB/s decoded response schedule. Proxy reaches page AND worker traffic. Exact limitations are in the audit;not calibrated iPhone/mobile-radio measurements. Synthetic deploy changes only worker cache suffix. Bootstrap cookies never enter output.

Resource and fault probes:

    python3 tools/perf/resources.py after-load > tools/perf/results/memory-after-load.json
    node tools/perf/client-probes.cjs > tools/perf/results/client-probes.json
    python3 tools/perf/restore-drill-probe.py > tools/perf/results/2026-11-05-restore-drill.json
    NODE_TLS_REJECT_UNAUTHORIZED=0 node tools/perf/http.mjs https://localhost:5443 tools/perf/results/duplicate-confirm.json --faults

Optional full-GC request,only after workload ends:

    dotnet tool install --tool-path /private/tmp/repa-perf-dotnet dotnet-gcdump --version 10.0.750501
    docker exec repa-perf-app dotnet /tools/.store/dotnet-gcdump/10.0.750501/dotnet-gcdump/10.0.750501/tools/net8.0/any/dotnet-gcdump.dll collect --process-id 1 -o /tmp/perf.gcdump
    python3 tools/perf/resources.py after-full-gc > tools/perf/results/memory-after-gc.json

Sample after diagnostic process exits. Its7.53MiB dump remains in tmpfs until restart,cgroup memory includes it. No dumps/traces committed. Audit tmpfs64MiB differs from production16MiB to fit diagnostics.

Native image footprint:

    docker build -f tools/perf/runtime.Dockerfile -t repa-perf-arm64 /private/tmp/repa-perf-publish
    docker image inspect repa-perf-arm64 --format '{{json .Size}}'

Production read-only aggregation and anonymous bounded probes:

    python3 tools/perf/production-probe.py > tools/perf/results/2026-11-05-production.json
    python3 tools/perf/public-probe.py > tools/perf/results/2026-11-05-public.json

Collector sets read-only/timeout PostgreSQL options and aggregates logs on wizard. No personal rows leave wizard. Do not use feedback CLI here:startup can migrate/seed before dispatch. Public requests are sequential,anonymous,bounded;never a production load test.

Full check with elapsed time:

    /usr/bin/time -p bash tools/check.sh\n\nFor five sequential elapsed-time samples instead:\n\n    python3 tools/perf/checks.py --runs 5

After measurement,remove only the disposable local audit containers:

    docker rm -f repa-perf-app repa-perf-db

startup.py's throwaway databases live in that isolated DB bind mount. Scratch files remain under /private/tmp for inspection. Audit image and scratch node/.NET tools are dev artifacts,not app dependencies.
