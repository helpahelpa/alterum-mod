#!/usr/bin/env bash
# Assemble a frame sequence recorded by the mod (ScreensDir/<tag>/frame_%04d.png) into a video.
#
#   ./make_video.sh <frames-dir> <output.mp4|.webm|.gif> [fps]
#
# fps must match the recording request (default 15, the game's own cap).
# mp4 uses libx264/yuv420p/+faststart; if libx264 is missing this falls back to webm (VP9, then
# VP8) next to the requested path instead of installing anything. A .webm or .gif target is
# honoured directly.
#
# Exit codes: 0 ok, 1 usage/input error, 2 no usable encoder.
set -uo pipefail

prog=$(basename "$0")
die() { printf '%s: %s\n' "$prog" "$*" >&2; exit 1; }

if [ $# -lt 2 ]; then
    printf 'usage: %s <frames-dir> <output.mp4|.webm|.gif> [fps]\n' "$prog" >&2
    exit 1
fi

dir=$1
out=$2
fps=${3:-15}

[ -d "$dir" ] || die "frames dir not found: $dir"
case "$fps" in
    ''|*[!0-9.]*|0|0.0) die "fps must be a positive number, got '$fps'" ;;
esac

command -v ffmpeg >/dev/null 2>&1 || {
    cat >&2 <<EOF
$prog: ffmpeg is not installed in this WSL distro.
Nothing was installed. Options:
  1) install it:            sudo apt-get install -y ffmpeg
  2) use the Windows build: /mnt/c/.../ffmpeg.exe with the same arguments
  3) skip assembly for now: the PNG sequence in '$dir' is the source of truth
EOF
    exit 2
}

# frame_0001.png -> prefix "frame", width 4, first index 1
shopt -s nullglob
frames=("$dir"/frame_*.png)
[ ${#frames[@]} -gt 0 ] || die "no frame_*.png in $dir"

first=$(basename "${frames[0]%.png}")
last=$(basename "${frames[${#frames[@]}-1]%.png}")
prefix=${first%_*}
firstnum=${first##*_}
lastnum=${last##*_}
width=${#firstnum}
start=$((10#$firstnum))
stop=$((10#$lastnum))
pattern="${prefix}_%0${width}d.png"

# Largest contiguous run from the first frame: ffmpeg stops at a gap, so refuse to pretend.
have=$(( ${#frames[@]} ))
run=0
for (( i = start; i <= stop; i++ )); do
    f=$(printf "%s/%s_%0${width}d.png" "$dir" "$prefix" "$i")
    [ -f "$f" ] || break
    run=$(( run + 1 ))
done
[ "$run" -gt 0 ] || die "no contiguous run starting at $first in $dir"
[ "$run" -eq "$have" ] || \
    printf '%s: warning: %d of %d frames are contiguous from %s; encoding the first %d\n' \
        "$prog" "$run" "$have" "$first" "$run" >&2

ffmpeg_ver=$(ffmpeg -hide_banner -version 2>/dev/null | head -n1)
printf '%s: %s\n' "$prog" "${ffmpeg_ver:-ffmpeg (version unknown)}"
printf '%s: %s frames %s at %s fps -> %s\n' "$prog" "$run" "$pattern" "$fps" "$out"

encoders=$(ffmpeg -hide_banner -encoders 2>/dev/null)
has_enc() { printf '%s\n' "$encoders" | grep -qE "^ *V[.A-Z]* +$1 "; }

input=( -hide_banner -loglevel warning -y -framerate "$fps" -start_number "$start" -i "$dir/$pattern" )
# libx264/yuv420p needs even dimensions; recordings already are, this only guards odd input.
even=( -vf "scale=trunc(iw/2)*2:trunc(ih/2)*2" )

ext=${out##*.}
target=$out
codec_args=()

case "$ext" in
    gif)
        has_enc gif || die "this ffmpeg has no gif encoder"
        target=$out
        # palette pass keeps the GIF from turning into mud
        palette=$(mktemp -t clip-palette-XXXXXX.png)
        ffmpeg "${input[@]}" -vf "fps=$fps,scale=trunc(iw/2)*2:trunc(ih/2)*2:flags=lanczos,palettegen" -y "$palette" \
            || { rm -f "$palette"; die "palettegen failed"; }
        ffmpeg "${input[@]}" -i "$palette" -lavfi "fps=$fps,scale=trunc(iw/2)*2:trunc(ih/2)*2:flags=lanczos [x]; [x][1:v] paletteuse" -y "$target" \
            || { rm -f "$palette"; die "gif encode failed"; }
        rm -f "$palette"
        ;;
    webm)
        if has_enc libvpx-vp9; then codec_args=( -c:v libvpx-vp9 -b:v 0 -crf 34 -pix_fmt yuv420p )
        elif has_enc libvpx; then codec_args=( -c:v libvpx -b:v 2M -pix_fmt yuv420p )
        else die "this ffmpeg has neither libvpx-vp9 nor libvpx"; fi
        target=$out
        ;;
    *)
        if has_enc libx264; then
            codec_args=( -c:v libx264 -pix_fmt yuv420p -movflags +faststart -crf 20 -preset medium )
            target=$out
        else
            # No libx264: keep the requested path but switch container to something we can encode.
            target="${out%.*}.webm"
            printf '%s: libx264 missing -> falling back to webm: %s\n' "$prog" "$target" >&2
            if has_enc libvpx-vp9; then codec_args=( -c:v libvpx-vp9 -b:v 0 -crf 34 -pix_fmt yuv420p )
            elif has_enc libvpx; then codec_args=( -c:v libvpx -b:v 2M -pix_fmt yuv420p )
            else die "no usable video encoder (libx264, libvpx-vp9, libvpx all missing)"; fi
        fi
        ;;
esac

if [ "$ext" = "gif" ]; then
    :   # already encoded above
else
    ffmpeg "${input[@]}" "${even[@]}" "${codec_args[@]}" "$target" || die "ffmpeg encode failed: $target"
fi

[ -s "$target" ] || die "output is missing or empty: $target"

size=$(stat -c %s "$target" 2>/dev/null || echo '?')
printf '%s: wrote %s (%s bytes)\n' "$prog" "$target" "$size"

if command -v ffprobe >/dev/null 2>&1; then
    ffprobe -v error -select_streams v:0 \
        -show_entries stream=codec_name,width,height,r_frame_rate,nb_frames \
        -show_entries format=duration,size -of default=noprint_wrappers=1 "$target"
else
    printf '%s: ffprobe not installed, skipping verification\n' "$prog" >&2
fi

printf '%s: %s\n' "$prog" "done: $(cd "$(dirname "$target")" && pwd)/$(basename "$target")"
