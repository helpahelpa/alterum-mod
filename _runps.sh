#!/bin/bash
# Run a local .ps1 on the Windows host over ssh (elevated) via -EncodedCommand.
set -euo pipefail
f="$1"; shift
b64=$(iconv -f utf-8 -t utf-16le "$f" | base64 -w0)
ssh windows "powershell -NoProfile -OutputFormat Text -ExecutionPolicy Bypass -EncodedCommand $b64" "$@" 2>&1 | grep -v "store now\|This session may be\|server may need to be upgraded\|^\*\*"
