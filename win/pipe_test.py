"""Question A: is yt-dlp | ffmpeg progressive?  Read-only experiment harness.

Usage:  python pipe_test.py <label> <url> [--nochunk] [--mode pipe|grow] [--startdelay N]

pipe : yt-dlp -o - <url> | ffmpeg -i - -f mp3 <out>
grow : yt-dlp -> partial file; ffmpeg -i <partfile> started after --startdelay s
Prints a JSON block with size-vs-time samples and derived time-to-first-N-seconds.
"""
import json, os, subprocess, sys, threading, time, shutil

BASE = r"C:\temp\alterum\music"
PLUG = r"C:\AlterumPinned\BepInEx\plugins\alterum-music"
YTDLP = os.path.join(PLUG, "yt-dlp.exe")
FFMPEG = os.path.join(PLUG, "ffmpeg.exe")
FFPROBE = os.path.join(PLUG, "ffprobe.exe")

label = sys.argv[1]
url = sys.argv[2]
nochunk = "--nochunk" in sys.argv
mode = "pipe"
if "--mode" in sys.argv:
    mode = sys.argv[sys.argv.index("--mode") + 1]
limit = None
if "--limit" in sys.argv:
    limit = sys.argv[sys.argv.index("--limit") + 1]
startdelay = 2.0
if "--startdelay" in sys.argv:
    startdelay = float(sys.argv[sys.argv.index("--startdelay") + 1])

work = os.path.join(BASE, label)
shutil.rmtree(work, ignore_errors=True)
os.makedirs(work, exist_ok=True)
out = os.path.join(work, "out.mp3")
part = os.path.join(work, "src.part")
log = os.path.join(work, "ff.log")

yt = [YTDLP, "--no-playlist", "--no-warnings", "--no-progress", "--no-mtime",
      "--socket-timeout", "15", "--retries", "2"]
if not nochunk:
    yt += ["--http-chunk-size", "1M"]
if limit:
    yt += ["--limit-rate", limit]
yt += ["-f", "bestaudio[ext=m4a]/bestaudio"]

ffc = [FFMPEG, "-hide_banner", "-nostats", "-nostdin", "-y", "-loglevel", "warning"]

errs = {}


def drain(name, pipe):
    data = pipe.read()
    errs[name] = data.decode("utf-8", "replace")


t0 = time.time()
if mode == "pipe":
    yt += ["-o", "-", url]
    yp = subprocess.Popen(yt, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    threading.Thread(target=drain, args=("yt", yp.stderr), daemon=True).start()
    fh = open(out, "wb")
    fp = subprocess.Popen(ffc + ["-i", "pipe:0", "-vn", "-c:a", "libmp3lame",
                                 "-b:a", "128k", "-f", "mp3", "pipe:1"],
                          stdin=yp.stdout, stdout=fh, stderr=subprocess.PIPE)
    threading.Thread(target=drain, args=("ff", fp.stderr), daemon=True).start()
    yp.stdout.close()
elif mode == "grow":
    yt += ["-o", part, "--keep-video", "--no-part", url]
    yp = subprocess.Popen(yt, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    threading.Thread(target=drain, args=("yt", yp.stderr), daemon=True).start()
    # wait for the file to appear + requested delay
    while not os.path.exists(part) and time.time() - t0 < 60:
        time.sleep(0.05)
    time.sleep(startdelay)
    tpose = time.time() - t0
    fh = open(out, "wb")
    # '-' input on a regular file: ffmpeg reads until EOF, then stops.
    fp = subprocess.Popen(ffc + ["-i", part, "-vn", "-c:a", "libmp3lame",
                                 "-b:a", "128k", "-f", "mp3", "pipe:1"],
                          stdout=fh, stderr=subprocess.PIPE)
    threading.Thread(target=drain, args=("ff", fp.stderr), daemon=True).start()

samples = []
srcsz = []
last = -1
t_ff_exit = None
while True:
    now = time.time() - t0
    try:
        sz = os.path.getsize(out)
    except OSError:
        sz = 0
    if mode == "grow" and int(now * 2) != int((now - 0.05) * 2):
        try:
            srcsz.append([round(now, 2), os.path.getsize(part)])
        except OSError:
            pass
    if sz != last:
        samples.append([round(now, 2), sz])
        last = sz
    if t_ff_exit is None and fp.poll() is not None:
        t_ff_exit = round(now, 2)
    if t_ff_exit is not None and yp.poll() is not None:
        break
    if now > 1800:
        errs["harness"] = "timeout"
        break
    time.sleep(0.05)

total = round(time.time() - t0, 2)
fh.close()
yt_exit = yp.poll()
ff_exit = fp.poll()
if yt_exit is None:
    subprocess.run(["taskkill", "/PID", str(yp.pid), "/T", "/F"], capture_output=True)
if ff_exit is None:
    subprocess.run(["taskkill", "/PID", str(fp.pid), "/T", "/F"], capture_output=True)

# validate
pr = subprocess.run([FFPROBE, "-v", "error", "-show_entries",
                     "format=duration,format_name,bit_rate:stream=codec_name,sample_rate,channels",
                     "-of", "json", out], capture_output=True)
probe = {}
try:
    probe = json.loads(pr.stdout.decode("utf-8", "replace"))
except Exception:
    probe["raw"] = pr.stdout.decode("utf-8", "replace") + pr.stderr.decode("utf-8", "replace")


def t_at(bytes_needed):
    for t, sz in samples:
        if sz >= bytes_needed:
            return t
    return None


res = {
    "label": label, "mode": mode, "nochunk": nochunk, "url": url,
    "yt_exit": yt_exit, "ff_exit": ff_exit,
    "t_ff_exit_s": t_ff_exit, "total_s": total,
    "final_bytes": last,
    "t_first_byte_s": samples[0][0] if samples else None,
    "t_16k_s": t_at(16000), "t_10s_s": t_at(16000 * 10), "t_30s_s": t_at(16000 * 30),
    "t_60s_s": t_at(16000 * 60),
    "samples_n": len(samples),
    "probe": probe,
}
if mode == "grow":
    res["ff_started_at_s"] = round(tpose, 2)
    res["src_bytes_when_ff_started"] = os.path.getsize(part) if os.path.exists(part) else None
    res["src_size_samples"] = srcsz[:12] + srcsz[12::20]
    res["src_bytes_final"] = os.path.getsize(part) if os.path.exists(part) else None
res["yt_stderr_tail"] = errs.get("yt", "")[-1200:]
res["ff_stderr_tail"] = errs.get("ff", "")[-1200:]
# compress samples: keep first 40, then every 10th
comp = samples[:40] + samples[40::10]
res["samples"] = comp
print("JSON_BEGIN")
print(json.dumps(res, indent=1))
print("JSON_END")
