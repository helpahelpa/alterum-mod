#!/bin/bash
# send a clip request to the cosmetics track install and wait for that clip to end
set -uo pipefail
REQ=/mnt/c/AlterumT3/BepInEx/screens/t3/clip.request
LOG=/mnt/c/AlterumT3/BepInEx/LogOutput.log
mark=$(grep -cE '\[clip\] (finish|stopped|start failed)' "$LOG" 2>/dev/null || echo 0)
printf '%s\n' "$@" > "$REQ"
for _ in $(seq 1 240); do
    sleep 0.5
    now=$(grep -cE '\[clip\] (finish|stopped|start failed)' "$LOG" 2>/dev/null || echo 0)
    if [ "${now:-0}" -gt "${mark:-0}" ]; then
        grep -E "\[clip\]" "$LOG" | tail -6
        exit 0
    fi
done
echo "TIMEOUT waiting for clip" >&2; grep -E "\[clip\]" "$LOG" | tail -8; exit 1
