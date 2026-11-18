import ctypes, json, os
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
f = os.path.join(W, "b.mp3")
open(f, "wb").close()
g = open(f, "ab")
import shutil
with open(SRC, "rb") as s:
    head = s.read(1_000_000)
g.write(head); g.flush()
snd = ctypes.c_void_p()
out = {}
out["1_fmod_open_while_writer_open"] = dll.FMOD_System_CreateStream(sysp, ctypes.c_char_p(f.encode()), 0x81, None, ctypes.byref(snd))
out["2_size_seen_by_writer"] = os.path.getsize(f)
try:
    dll.FMOD_System_CreateStream(sysp, ctypes.c_char_p(f.encode()), 0x81, None, ctypes.byref(ctypes.c_void_p()))
    out["3_second_fmod_stream"] = "ok"
except Exception as e:
    out["3_second_fmod_stream"] = str(e)
g.close()
print(json.dumps(out))
dll.FMOD_System_Close(sysp)
dll.FMOD_System_Release(sysp)
