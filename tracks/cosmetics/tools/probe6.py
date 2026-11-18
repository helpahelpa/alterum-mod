import UnityPy, json
env = UnityPy.load("unpack_bundle_3110260")
for o in env.objects:
    if o.type.name != "Mesh" or o.path_id != 118: continue
    d = o.read_typetree()
    def short(v, depth=0):
        if isinstance(v, dict):
            return {k: short(x, depth+1) for k,x in v.items() if not isinstance(x,(bytes,bytearray))}
        if isinstance(v, list):
            return f"list[{len(v)}]" if len(v)>8 else [short(x,depth+1) for x in v]
        if isinstance(v,(bytes,bytearray)): return f"bytes[{len(v)}]"
        return v
    print(json.dumps(short(d), indent=1)[:4000])
