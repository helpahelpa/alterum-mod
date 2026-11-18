import UnityPy, sys, time, collections
path = sys.argv[1]
t0=time.time()
env = UnityPy.load(path)
print("load:", round(time.time()-t0,1),"s files:", len(env.files))
for f in env.files.values():
    print("  ", type(f).__name__, getattr(f,"name","?"), "ver", getattr(f,"version","?"), "unity", getattr(f,"unity_version","?"))
names=[]
for obj in env.objects:
    if obj.type.name != "AnimationClip": continue
    try:
        tt = obj.read_typetree()
        nm = tt.get("m_Name")
    except Exception as e:
        nm = "<err>"
    names.append(nm)
print("clips:", len(names))
for n in sorted(names)[:400]: print("  ", n)
