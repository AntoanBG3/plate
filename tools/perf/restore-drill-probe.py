#!/usr/bin/env python3
"""Run: python3 tools/perf/restore-drill-probe.py

Exercises the existing nightly-drill decision using a fake Docker executable in
a scratch directory. Creates no containers and touches no production files.
Success means the probe reproduced an incorrect Restore OK on a missing table.
"""
import json
import os
import pathlib
import subprocess
import tempfile

root = pathlib.Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix="repa-restore-probe-") as directory:
    docker = pathlib.Path(directory, "docker")
    docker.write_text('''#!/usr/bin/env python3
import sys
args=sys.argv[1:]
if "readlink" in args: print("synthetic-nightly")
elif "cat" in args: print("synthetic archive; not read by real pg_restore")
elif args[:2]==["run","-d"]: print("synthetic-container")
elif "gymtracker-db" in args and "psql" in " ".join(args): print("FoodLog=38 Profiles=5")
elif "gymtracker-restore-drill" in args and "psql" in args: print("Profiles=5")
sys.exit(0)
''')
    docker.chmod(0o700)
    environment = dict(os.environ, PATH=directory + os.pathsep + os.environ["PATH"])
    result = subprocess.run(["bash", str(root / "tools/restore-drill.sh"), "nightly"],
                            env=environment, text=True, capture_output=True, timeout=15)
    reproduced = result.returncode == 0 and "Restore OK. Counts differ" in result.stdout
    print(json.dumps({"probe": "nightly restore missing a whole table", "environment": "fake docker; existing shell script unchanged",
                      "live_manifest": "FoodLog=38 Profiles=5", "restored_manifest": "Profiles=5",
                      "drill_exit_code": result.returncode, "false_success_reproduced": reproduced,
                      "stdout": result.stdout}, indent=2))
    raise SystemExit(0 if reproduced else 1)
