import UnityPy, sys, collections
for path in sys.argv[1:]:
    print("="*70); print(path)
    env = UnityPy.load(path)
    print("unity_version:", getattr(env, "unity_version", "?"), "files:", len(env.files))
    c = collections.Counter()
    for f in env.files.values():
        sf = getattr(f, "files", None)
        if sf:
            for k,v in sf.items():
                try: print("  serialized:", k, "ver", getattr(v,"version","?"), "fmt", getattr(v,"format","?"), "bigid", getattr(v,"endian","?"))
                except Exception as e: print("  ", e)
    for obj in env.objects:
        c[obj.type.name] += 1
    for k,v in c.most_common(): print(f"  {k}: {v}")
