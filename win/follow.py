"""Decisive: while FMOD plays an mp3 that is still being appended, does it
(a) stop at the file size it saw at open, or (b) keep playing past it?

Phase 1 uses a producer FASTER than realtime (25x) - the production case.
Phase 2 uses a producer SLOWER than realtime (0.4x) - the pathological case.
"""
import ctypes, json, os, shutil, sys, threading, time

DLL = r"C:\AlterumPinned\Alterum_Data\Plugins\x86_64\fmodstudio.dll"
SRC = r"C:\temp\alterum\music\bbb_pipe\out.mp3"
W = r"C:\temp\alterum\music\follow"
dll = ctypes.WinDLL(DLL)
c_void_pp = ctypes.POINTER(ctypes.c_void_p)
dll.FMOD_System_Create.argtypes = [c_void_pp, ctypes.c_uint]
dll.FMOD_System_CreateStream.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_uint, ctypes.c_void_p, c_void_pp]
dll.FMOD_System_PlaySound.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int, c_void_pp]
dll.FMOD_Channel_GetPosition.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint), ctypes.c_uint]
dll.FMOD_Channel_IsPlaying.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_int)]
sysp = ctypes.c_void_p()
assert dll.FMOD_System_Create(ctypes.byref(sysp), 0x00020300) == 0
assert dll.FMOD_System_Init(sysp, 64, 0, None) == 0
os.makedirs(W, exist_ok=True)
TOTAL = os.path.getsize(SRC)


def pos(ch):
    p = ctypes.c_uint(0)
    dll.FMOD_Channel_GetPosition(ch, ctypes.byref(p), 1)
    return p.value


def playing(ch):
    b = ctypes.c_int(0)
    dll.FMOD_Channel_IsPlaying(ch, ctypes.byref(b))
    return bool(b.value)


def run(label, start_bytes, rate_Bps, watch_s):
    grow = os.path.join(W, "g.mp3")
    if os.path.exists(grow):
        os.remove(grow)
    with open(SRC, "rb") as f, open(grow, "wb") as g:
        g.write(f.read(start_bytes))
    stop = threading.Event()
    g = open(grow, "ab")   # writer handle taken BEFORE FMOD opens the file
    def producer():
        with open(SRC, "rb") as f:
            f.seek(start_bytes)
            sent = start_bytes
            t0 = time.time()
            while sent < TOTAL and not stop.is_set():
                c = f.read(16384)
                if not c:
                    break
                g.write(c); g.flush(); sent += len(c)
                lag = t0 + (sent - start_bytes) / rate_Bps - time.time()
                if lag > 0:
                    time.sleep(lag)

    def guarded():
        try:
            producer()
        except Exception:
            import traceback; traceback.print_exc()
    threading.Thread(target=guarded, daemon=True).start()
    snd, ch = ctypes.c_void_p(), ctypes.c_void_p()
    rc = dll.FMOD_System_CreateStream(sysp, ctypes.c_char_p(grow.encode()), 0x81, None, ctypes.byref(snd))
    rp = dll.FMOD_System_PlaySound(sysp, snd, None, 0, ctypes.byref(ch))
    t0 = time.time()
    walk, stalls = [], []
    last_p, last_t = -1, t0
    while time.time() - t0 < watch_s:
        dll.FMOD_System_Update(sysp)
        p = pos(ch)
        pl = playing(ch)
        walk.append([round(time.time() - t0, 2), p, pl, os.path.getsize(grow)])
        if last_p >= 0 and p == last_p and pl and time.time() - last_t > 0.5:
            stalls.append(round(time.time() - t0, 2))
            last_t = time.time()
        if p != last_p:
            last_p, last_t = p, time.time()
        if not pl:
            break
        time.sleep(0.1)
    stop.set()
    g.close()
    out = {
        "label": label, "create": rc, "play": rp,
        "start_bytes": start_bytes, "rate_Bps": rate_Bps,
        "start_audio_s": round(start_bytes / 16000, 1),
        "watched_s": walk[-1][0] if walk else None,
        "final_pos_ms": walk[-1][1] if walk else None,
        "final_playing": walk[-1][2] if walk else None,
        "final_file_bytes": walk[-1][3] if walk else None,
        "playback_ratio": round(((walk[-1][1] / 1000.0) / walk[-1][0]), 3) if walk and walk[-1][0] else None,
        "stall_events": stalls[:10],
        "walk": walk[:: max(1, len(walk) // 12)],
    }
    print(json.dumps(out))


run("fast_producer_25x_realtime", 262144, 400_000, 40)
run("slow_producer_0.4x_realtime", 262144, 6400, 40)
dll.FMOD_System_Close(sysp)
dll.FMOD_System_Release(sysp)
