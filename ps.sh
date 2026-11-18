#!/bin/bash
f="$1"
b64=$(iconv -f UTF-8 -t UTF-16LE "$f" | base64 -w0)
ssh windows "powershell -NoProfile -OutputFormat Text -EncodedCommand $b64" 2>/dev/null
