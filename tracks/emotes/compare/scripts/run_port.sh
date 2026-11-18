#!/bin/bash
# Drive the ported clips through the existing emote.request path and copy the joint dumps.
set -u
S=/mnt/c/AlterumT2/BepInEx/screens/t2
L=/mnt/c/AlterumT2/BepInEx/LogOutput.log
TAG="${1:-fixed}"
shift || true
FLAGS="$*"
cd "$(dirname "$0")/../.."
mkdir -p compare/port
declare -A DUR=( [Clap]=1.00 [D_Clap]=1.00 [D_Middle_Finger]=0.87 [D_Sign]=3.15 [Idle1Fix]=4.03 [Middle_Finger]=0.87 [Prisyadka]=6.75 [Salute]=1.78 [Shy]=2.50 [Sign]=2.08 [The_Griddy]=6.53 [Twerk]=0.25 )
for clip in Clap D_Clap D_Middle_Finger D_Sign Idle1Fix Middle_Finger Prisyadka Salute Shy Sign The_Griddy Twerk; do
  d=${DUR[$clip]}
  reps=$(python3 -c "import math;print(max(1,math.ceil(2.0/$d)))")
  rm -f "$S/emote_joints_$clip.csv"
  { echo "clip=$clip"; echo "dump=1"; echo "repeats=$reps"; for f in $FLAGS; do echo "$f"; done; } > "$S/emote.request"
  wait=$(( $(python3 -c "print(int($d*$reps+2.8))") ))
  sleep $wait
  if [ -f "$S/emote_joints_$clip.csv" ]; then cp "$S/emote_joints_$clip.csv" "compare/port/${clip}_${TAG}.csv"; echo "$clip reps=$reps wait=${wait}s frames=$(wc -l < compare/port/${clip}_${TAG}.csv)";
  else echo "$clip MISSING dump"; fi
done
