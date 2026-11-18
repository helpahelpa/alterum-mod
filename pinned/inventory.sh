#!/bin/bash
# inventory one build dir
d="$1"
echo "### $d"
if [ ! -d "$d" ]; then echo "MISSING"; return 1 2>/dev/null || exit 1; fi
bytecount=$(du -sb "$d" 2>/dev/null | cut -f1)
files=$(find "$d" -type f 2>/dev/null | wc -l)
echo "bytes=$bytecount files=$files"
ga="$d/GameAssembly.dll"; gm="$d/Alterum_Data/il2cpp_data/Metadata/global-metadata.dat"
for f in "$ga" "$gm"; do
  if [ -f "$f" ]; then
    sz=$(stat -c%s "$f"); h=$(sha256sum "$f" | cut -d' ' -f1)
    echo "$(basename $f) size=$sz sha256=$h"
  else echo "$(basename $f) ABSENT"; fi
done
gg="$d/Alterum_Data/globalgamemanagers"
if [ -f "$gg" ]; then
  uv=$(strings -n 5 "$gg" 2>/dev/null | grep -m1 -E '^20[0-9]{2}\.[0-9]+\.[0-9]+[abfp][0-9]+$')
  echo "unity_from_gg=$uv"
fi
# UnityPlayer.dll product version
up="$d/UnityPlayer.dll"
if [ -f "$up" ]; then
  sv=$(strings -el -n 7 "$up" 2>/dev/null | grep -m1 -E '^20[0-9]{2}\.[0-9]+\.[0-9]+[abfp][0-9]+' )
  echo "unity_from_unityplayer=$sv"
fi
pdbs=$(find "$d" -name '*.pdb' -o -name '*.debug' 2>/dev/null | head -3)
echo "pdb_or_debug=${pdbs:-none}"
for m in "$d/Alterum_Data/Managed" "$d/Managed"; do [ -d "$m" ] && echo "Managed_dir=$m ($(find "$m" -type f | wc -l) files)"; done
[ -f "$d/Alterum_Data/il2cpp_data/Metadata/global-metadata.dat" ] || echo "NO_METADATA"
# appmanifest
for acf in "$d"/steamapps/appmanifest_4424420.acf; do
  [ -f "$acf" ] && { echo "acf=$acf"; grep -E '"(buildid|TargetBuildID|SizeOnDisk|manifest|BetaKey|StateFlags)"' "$acf" | tr -d '\t' | sed 's/  */ /g'; }
done
echo
