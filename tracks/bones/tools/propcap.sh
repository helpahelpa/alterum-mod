#!/bin/bash
# Send one propcap.request to the bones-track install (T1) and wait for the response.
#
#   tools/propcap.sh <<'EOF'
#   spawn=Female_3_Diana
#   freeze=1
#   measure=1
#   EOF
#
# Prints the propcap/bounds/jsolve log lines produced by the request. Waits until the log has
# been quiet for a moment (captures take ~1 s per view) or the timeout expires.
set -uo pipefail
SCREENS=/mnt/c/AlterumT1/BepInEx/screens/t1
LOG=/mnt/c/AlterumT1/BepInEx/LogOutput.log
TIMEOUT=${PROPCAP_TIMEOUT:-120}

body="$(cat)"
[ -z "$body" ] && { echo "empty request on stdin" >&2; exit 2; }

start=$(wc -l < "$LOG")
printf '# %s %s\n%s\n' "$$" "$(date +%s%N)" "$body" > "$SCREENS/propcap.request"

last=$start; quiet=0; elapsed=0
while [ "$elapsed" -lt $((TIMEOUT * 2)) ]; do
    sleep 0.5; elapsed=$((elapsed + 1))
    now=$(wc -l < "$LOG")
    if [ "$now" -ne "$last" ]; then last=$now; quiet=0; continue; fi
    quiet=$((quiet + 1))
    [ "$quiet" -ge 3 ] && [ "$now" -gt "$start" ] && break
done
tail -n +$((start + 1)) "$LOG" | grep -E '\[propcap|\[bounds:(apply|scale|measure|verify|restore)|\[jsolve' | sed -e 's/^\[[^]]*\] //'
