import ctypes
DLL = r"C:\AlterumPinned\Alterum_Data\Plugins\x86_64\fmodstudio.dll"
dll = ctypes.WinDLL(DLL)
dll.FMOD_System_Create.argtypes = [ctypes.POINTER(ctypes.c_void_p), ctypes.c_uint]
for minor in range(0, 12):
    for patch in range(0, 40):
        v = 0x00020000 | (minor << 8) | patch
        s = ctypes.c_void_p()
        r = dll.FMOD_System_Create(ctypes.byref(s), v)
        if r == 0:
            print("OK version", hex(v), "= 2.%d.%d" % (minor, patch))
            dll.FMOD_System_Release(s)
            raise SystemExit(0)
print("no version matched")
