"""Question A (playback half): can FMOD play an MP3 that is still being written?

Read-only: uses the game's own fmodstudio.dll (FMOD Studio 2.3.0) from a separate process;
the game is not running. Producer = slow copier appending to grow.mp3 at RATE bytes/s
(the real pipeline encodes at ~50x realtime, so RATE is generous). Consumer = createStream
+ playSound + getPosition polling.
"""
import ctypes, json, os, shutil, sys, threading, time

DLL = r"C:\AlterumPinned\Alterum_Data\Plugins\x86_64\fmodstudio.dll"
SRC = r"C:\temp\alterum\music\bbb_pipe\out.mp3"
WORK = r"C:\temp\alterum\music\growplay"
RATE = int(sys.argv[1]) if len(sys.argv) > 1 else 400_000
MODE_CS = 0x80 | 0x1     # CREATESTREAM | LOOP_OFF
MODE_CS_NB = MODE_CS | 0x10000  # | NONBLOCKING

dll = ctypes.WinDLL(DLL)
c_int_p, c_uint_p, c_void_pp = ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_uint), ctypes.POINTER(ctypes.c_void_p)
dll.FMOD_System_Create.argtypes = [c_void_pp, ctypes.c_uint]
dll.FMOD_System_CreateStream.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_uint, ctypes.c_void_p, c_void_pp]
dll.FMOD_System_PlaySound.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int, c_void_pp]
dll.FMOD_Channel_GetPosition.argtypes = [ctypes.c_void_p, c_uint_p, ctypes.c_uint]
dll.FMOD_Channel_IsPlaying.argtypes = [ctypes.c_void_p, c_int_p]
dll.FMOD_Sound_GetOpenState.argtypes = [ctypes.c_void_p, c_int_p, c_uint_p, c_int_p, c_int_p]
dll.FMOD_Channel_SetPosition.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_uint]

sysp = ctypes.c_void_p()
assert dll.FMOD_System_Create(ctypes.byref(sysp), 0x00020300) == 0
assert dll.FMOD_System_Init(sysp, 64, 0, None) == 0

os.makedirs(WORK, exist_ok=True)
grow = os.path.join(WORK, "grow.mp3")
src = os.path.join(WORK, "src.mp3")
shutil.copyfile(SRC, src)
if os.path.exists(grow):
    os.remove(grow)
total = os.path.getsize(src)

stop = threading.Event()


def producer():
    f = open(src, "rb")
    d = open(grow, "wb")
    sent, t0 = 0, time.time()
    while sent < total and not stop.is_set():
        ch = f.read(16384)
        if not ch:
            break
        d.write(ch); d.flush(); sent += len(ch)
        lag = t0 + sent / RATE - time.time()
        if lag > 0:
            time.sleep(lag)
    d.close(); f.close()


def pos_of(ch):
    p = ctypes.c_uint(0)
    dll.FMOD_Channel_GetPosition(ch, ctypes.byref(p), 1)
    return p.value


def playing_of(ch):
    b = ctypes.c_int(0)
    dll.FMOD_Channel_IsPlaying(ch, ctypes.byref(b))
    return bool(b.value)


def fsize():
    try:
        return os.path.getsize(grow)
    except OSError:
        return 0


open(grow, "wb").close()   # create before FMOD opens it
th = threading.Thread(target=producer, daemon=True)
th.start()
while os.path.getsize(grow) == 0:
    time.sleep(0.01)
t0 = time.time()
snd, chan = ctypes.c_void_p(), ctypes.c_void_p()
print("createStream", dll.FMOD_System_CreateStream(sysp, ctypes.c_char_p(grow.encode()), MODE_CS, None, ctypes.byref(snd)))
r = 0
while time.time() - t0 < 10:
    dll.FMOD_System_Update(sysp)
    r = dll.FMOD_System_PlaySound(sysp, snd, None, 0, ctypes.byref(chan))
    if r == 0:
        break
    time.sleep(0.02)
t_play = round(time.time() - t0, 2)
print("playSound", r, "after", t_play, "s; file", fsize())

ops, pct, st, db = ctypes.c_int(-1), ctypes.c_uint(0), ctypes.c_int(0), ctypes.c_int(0)
dll.FMOD_Sound_GetOpenState(snd, ctypes.byref(ops), ctypes.byref(pct), ctypes.byref(st), ctypes.byref(db))
print("openstate", ops.value, "pct", pct.value)

hist, ended = [], None
while time.time() - t0 < 60:
    dll.FMOD_System_Update(sysp)
    p, pl, fs = pos_of(chan), playing_of(chan), fsize()
    hist.append([round(time.time() - t0, 2), p, pl, fs])
    if not pl and ended is None:
        ended = round(time.time() - t0, 2)
        if fs >= total:
            break
        # reopen at the position we reached and keep going
        snd2, ch2 = ctypes.c_void_p(), ctypes.c_void_p()
        rr = dll.FMOD_System_CreateStream(sysp, ctypes.c_char_p(grow.encode()), MODE_CS, None, ctypes.byref(snd2))
        rr2 = dll.FMOD_System_PlaySound(sysp, snd2, None, 0, ctypes.byref(ch2))
        rr3 = dll.FMOD_Channel_SetPosition(ch2, ctypes.c_uint(p), 1)
        print("REOPEN", rr, rr2, rr3, "from", p, "at", round(time.time() - t0, 2))
        chan = ch2
    if ended is not None and fs >= total and not playing_of(chan):
        break
    time.sleep(0.1)
stop.set()

res = {
    "rate_Bps": RATE, "src_bytes": total, "src_seconds": 634.7,
    "t_play_s": t_play, "file_bytes_at_play": hist[0][3] if hist else None,
    "first_reached_end_s": ended, "pos_at_first_end_ms": hist[0][1] if hist else None,
    "final_pos_ms": hist[-1][1] if hist else None,
    "final_playing": hist[-1][2] if hist else None,
    "final_file_bytes": hist[-1][3] if hist else None,
    "n": len(hist),
}
res["pos_at_first_end_ms"] = next((h[1] for h in hist if h[0] == ended), None)
res["walk"] = hist[:: max(1, len(hist) // 20)]
print("JSON_BEGIN")
print(json.dumps(res, indent=1))
print("JSON_END")
dll.FMOD_System_Close(sysp)
dll.FMOD_System_Release(sysp)
try:
    os.remove(src)
except OSError:
    pass
