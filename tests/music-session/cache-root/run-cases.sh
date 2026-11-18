#!/bin/bash
# Regression loop for the shipped music-cache override. Every case runs the real process with the
# real launch argument shape used by the provisioned pair; the binary asserts the shipping property.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_UI_LANGUAGE=en
export PATH="$HOME/.dotnet:$PATH"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
fails=0
run() {
  local name="$1"; shift
  echo "== $name"
  dotnet "$DLL" "$@"
  local rc=$?
  [ $rc -eq 0 ] || { echo "FAILED: $name (exit $rc)"; fails=$((fails+1)); }
}
# build once, no shared compiler / MSBuild server
dotnet build "$HERE/CacheRoot.RealPath.csproj" -c Release -v q --nologo \
  -nodeReuse:false -p:UseSharedCompilation=false || exit 90
BASE="$(cd "$HERE/bin/Release/net6.0" && pwd)"
DLL="$BASE/MusicCacheRoot.RealPath.dll"
OWN="$BASE/BepInEx/music-cache"
mkdir -p "$OWN/stream" "$OWN/done" || exit 91

run "default (no override)"                 --expect=default
run "arg inside own game root (provisioner shape)" --expect="$OWN" "-alterumMusicCache=$OWN"
run "arg outside own game root rejected"    --expect=default "-alterumMusicCache=$TMP/music-cache"
run "relative arg rejected"                 --expect=default "-alterumMusicCache=BepInEx/music-cache"
run "windows drive-relative C:cache rejected" --expect=default "-alterumMusicCache=C:cache"
run "windows root-relative \\cache rejected"  --expect=default '-alterumMusicCache=\cache'
run "windows path-form classifier (semantic)" --windows-forms
run "traversal arg rejected"                --expect=default "-alterumMusicCache=$BASE/../music-cache"
run "duplicate arg rejected"                --expect=default "-alterumMusicCache=$OWN" "-alterumMusicCache=$OWN"
echo "== env inside own game root"
ALTERUM_MUSIC_CACHE="$OWN" dotnet "$DLL" --expect="$OWN" || { echo "FAILED: env inside own game root"; fails=$((fails+1)); }
echo "== arg wins over env"
ALTERUM_MUSIC_CACHE="$TMP/env-cache" dotnet "$DLL" --expect="$OWN" "-alterumMusicCache=$OWN" || { echo "FAILED: arg wins over env"; fails=$((fails+1)); }
echo "== env outside own game root rejected"
ALTERUM_MUSIC_CACHE="$TMP/env-cache" dotnet "$DLL" --expect=default || { echo "FAILED: env outside"; fails=$((fails+1)); }

echo
if [ $fails -eq 0 ]; then echo "PASS all cache-root cases"; exit 0; fi
echo "FAIL $fails cache-root case(s)"; exit 1
