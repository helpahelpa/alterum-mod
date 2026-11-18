"""Sharing matrix: can FMOD open a file while a writer handle is open, and vice versa?"""
import ctypes, json, os, shutil

DLL = r"C:\AlterumPinned\Alterum_Data\Plugins\x86_64\fmodstudio.dll"
SRC = r"C:\temp\alterum\music\bbb_pipe\out.mp3"
W = r"C:\temp\alterum\music\share"
dll = ctypes.WinDLL(DLL)
c_void_pp = ctypes.POINTER(ctypes.c_void_p)
dll.FMOD_System_Create.argtypes = [c_void_pp, ctypes.c_uint]
dll.FMOD_System_CreateStream.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_uint, ctypes.c_void_p, c_void_pp]
sysp = ctypes.c_void_p()
assert dll.FMOD_System_Create(ctypes.byref(sysp), 0x00020300) == 0
assert dll.FMOD_System_Init(sysp, 64, 0, None) == 0
os.makedirs(W, exist_ok=True)
f = os.path.join(W, "a.mp3")
with open(SRC, "rb") as s, open(f, "wb") as g:
    g.write(s.read(1_000_000))
out = {}


def cre():
    snd = ctypes.c_void_p()
    return dll.FMOD_System_CreateStream(sysp, ctypes.c_char_p(f.encode()), 0x81, None, ctypes.byref(snd))


out["fmod_alone"] = cre()

g = open(f, "ab")            # writer handle open, no growth
out["fmod_with_writer_handle_open"] = cre()
g.write(os.urandom(4096)); g.flush()
out["fmod_after_writer_append"] = cre()
g.close()
out["fmod_after_writer_closed"] = cre()

# reverse order: FMOD first (leave a live stream), then open a writer
snd = ctypes.c_void_p()
out["fmod_reopen"] = dll.FMOD_System_CreateStream(sysp, ctypes.c_char_p(f.encode()), 0x81, None, ctypes.byref(snd))
try:
    g2 = open(f, "ab")
    g2.write(b"\0" * 1024); g2.flush(); g2.close()
    out["writer_with_live_fmod_stream"] = "ok"
except Exception as e:
    out["writer_with_live_fmod_stream"] = "%s: %s" % (type(e).__name__, e)
print(json.dumps(out, indent=1))
dll.FMOD_System_Close(sysp)
dll.FMOD_System_Release(sysp)
