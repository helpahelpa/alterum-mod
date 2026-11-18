#!/bin/bash
# Send one line to the running game's music.request and echo it with a wall-clock stamp.
# Usage: mreq.sh 'stream=C:\temp\alterum\music\t52.mp3'
F=/mnt/c/AlterumPinned/BepInEx/screens/music.request
mkdir -p /mnt/c/AlterumPinned/BepInEx/screens
printf '%s\n' "$1" > "$F"
printf '[%s] -> %s\n' "$(date +%H:%M:%S.%3N)" "$1"
