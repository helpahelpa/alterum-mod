import UnityPy, sys, collections, zlib
for path in sys.argv[1:]:
    env = UnityPy.load(path)
    g, t = {}, {}
    for obj in env.objects:
        if obj.type.name=="GameObject": g[obj.path_id]=obj.read_typetree()
        elif obj.type.name=="Transform": t[obj.path_id]=obj.read_typetree()
    names = collections.Counter()
    for pid,tt in t.items():
        go=tt.get("m_GameObject"); gid=go.get("m_PathID",0) if isinstance(go,dict) else 0
        names[g.get(gid,{}).get("m_Name","?")] += 1
    print("="*70); print(path, "transforms:", len(t))
    print("  names:", ", ".join(f"{k}x{v}" for k,v in sorted(names.items())))
