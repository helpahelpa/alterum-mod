"""Probe: which file states does FMOD createStream accept? (mp3 from the pipe)"""
import ctypes, os, shutil, threading, time, json

DLL = r"C:\AlterumPinned\Alterum_Data\Plugins\x86_64\fmodstudio.dll"
SRC = r"C:\temp\alterum\music\bbb_pipe\out.mp3"
W = r"C:\temp\alterum\music\probe"
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


def try_stream(path, label, play=True):
    snd, chan = ctypes.c_void_p(), ctypes.c_void_p()
    r = dll.FMOD_System_CreateStream(sysp, ctypes.c_char_p(path.encode()), 0x81, None, ctypes.byref(snd))
    out = {"label": label, "create": r}
    if r == 0 and play:
        r2 = dll.FMOD_System_PlaySound(sysp, snd, None, 0, ctypes.byref(chan))
        out["play"] = r2
        end, pos = time.time() + 1.2, 0
        while time.time() < end:
            dll.FMOD_System_Update(sysp)
            p = ctypes.c_uint(0)
            dll.FMOD_Channel_GetPosition(chan, ctypes.byref(p), 1)
            pos = p.value
            time.sleep(0.05)
        out["pos_ms_after_1.2s"] = pos
    print(json.dumps(out))


for n in (16384, 262144, 2_000_000):
    p = os.path.join(W, "trunc%d.mp3" % n)
    with open(SRC, "rb") as f, open(p, "wb") as g:
        g.write(f.read(n))
    try_stream(p, "static_trunc_%d" % n)

full = os.path.join(W, "full.mp3")
shutil.copyfile(SRC, full)
try_stream(full, "static_full")

# growing: producer appends to a file that already has 2 MB
grow = os.path.join(W, "grow.mp3")
with open(SRC, "rb") as f:
    head = f.read(2_000_000)
open(grow, "wb").write(head)
stop = threading.Event()


def producer():
    with open(SRC, "rb") as f, open(grow, "ab") as g:
        f.seek(2_000_000)
        sent = 2_000_000
        t0 = time.time()
        while sent < os.path.getsize(SRC) and not stop.is_set():
            ch = f.read(16384)
            if not ch:
                break
            g.write(ch); g.flush(); sent += len(ch)
            lag = t0 + (sent - 2_000_000) / 400_000 - time.time()
            if lag > 0:
                time.sleep(lag)


th = threading.Thread(target=producer, daemon=True)
th.start()
try_stream(grow, "growing_after_2MB")
stop.set()
