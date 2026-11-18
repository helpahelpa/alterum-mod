#!/usr/bin/env python3
"""Run a real QC command through a running instance's local developer mailbox.

Examples (quote the complete console command):
  python3 win/console.py --root /mnt/c/AlterumPinned 'help'
  python3 win/console.py --root /mnt/c/AlterumB --instance b 'alterum.test.identity'

No UI input or persistent unlock. Prints the command's synchronous log output.
A successful exit means dispatch completed, NOT that an asynchronous action finished.
Requires the Hot version implementing `exec <request-id> <command>`.
"""
import argparse
import os
from pathlib import Path
import re
import sys
import time
import uuid


def execute(root, instance, command, timeout=15, log_path=None, emit=print):
    root = Path(root)
    if not (root / 'Alterum.exe').is_file():
        raise ValueError('root must be a game install containing Alterum.exe')
    if instance and not re.fullmatch(r'[A-Za-z0-9_-]+', instance):
        raise ValueError('instance must be a single safe slot name')
    if not command.strip() or any(c in command for c in '\r\n\0'):
        raise ValueError('provide exactly one nonempty console command (no line breaks)')
    if timeout <= 0:
        raise ValueError('timeout must be positive')
    screens = root / 'BepInEx' / 'screens'
    if instance:
        screens /= instance
    if not screens.is_dir():
        raise ValueError('instance screens directory does not exist; launch this instance first')
    log = Path(log_path) if log_path else root / 'BepInEx' / (
        f'logs/{instance}.log' if instance else 'LogOutput.log')
    offset = log.stat().st_size
    request_id = uuid.uuid4().hex
    request = screens / 'qc.request'
    temporary = screens / f'.qc-exec-{request_id}.tmp'
    deadline = time.monotonic() + timeout
    try:
        temporary.write_text(f'exec {request_id} {command.strip()}\n', encoding='utf-8')
        while True:
            try:
                # Atomic publication with NO overwrite: concurrent callers and older requests
                # cannot be lost. The consumer only sees a complete, closed request file.
                os.link(temporary, request)
                break
            except FileExistsError:
                if time.monotonic() >= deadline:
                    raise TimeoutError('mailbox busy; existing request was not overwritten')
                time.sleep(0.05)
    finally:
        temporary.unlink(missing_ok=True)

    begin = f'[qc:direct] id={request_id} state=begin'
    terminal = re.compile(rf'\[qc:direct\] id={request_id} state=(dispatched|error)\b')
    pending = b''
    started = False
    assertion_failed = False
    execution_error = False
    while time.monotonic() < deadline:
        if log.stat().st_size < offset:
            raise RuntimeError('log was truncated during execution; dispatch outcome is unknown')
        with log.open('rb') as stream:
            stream.seek(offset)
            data = stream.read()
            offset = stream.tell()
        pending += data
        while b'\n' in pending:
            line, pending = pending.split(b'\n', 1)
            text = line.decode('utf-8', errors='replace').rstrip('\r')
            if begin in text:
                started = True
            if started:
                emit(text)
                if '[qc:test]' in text and 'result=FAIL' in text:
                    assertion_failed = True
                if re.search(r'\[Error\s*[:\]]', text) or text.startswith('Exception:'):
                    execution_error = True
                result = terminal.search(text)
                if result:
                    return 1 if result[1] == 'error' or assertion_failed or execution_error else 0
        time.sleep(0.05)
    raise TimeoutError(f'no dispatch acknowledgement for {request_id}; command may still execute; do not blindly retry mutations')


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--root', required=True, type=Path)
    parser.add_argument('--instance', default='')
    parser.add_argument('--log', type=Path, help='override the instance log location')
    parser.add_argument('--timeout', type=float, default=15)
    parser.add_argument('command', help='one quoted QC command, with its arguments')
    args = parser.parse_args()
    try:
        return execute(args.root, args.instance, args.command, args.timeout, args.log)
    except (OSError, ValueError, RuntimeError, TimeoutError) as error:
        print(f'console: {error}', file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
