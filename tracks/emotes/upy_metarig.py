import UnityPy, zlib, collections, json
env = UnityPy.load("../../tracks/cosmetics/unpack_bundle_70132")
g, t = {}, {}
for obj in env.objects:
    if obj.type.name=="GameObject": g[obj.path_id]=obj.read_typetree()
    elif obj.type.name=="Transform": t[obj.path_id]=obj.read_typetree()

def fp(tt):
    f=tt.get("m_Father"); return f.get("m_PathID",0) if isinstance(f,dict) else 0
def nm(pid):
    go=t[pid].get("m_GameObject"); gid=go.get("m_PathID",0) if isinstance(go,dict) else 0
    return g.get(gid,{}).get("m_Name","?")
kids=collections.defaultdict(list)
for pid,tt in t.items(): kids[fp(tt)].append(pid)
roots=[p for p in t if fp(t[p])==0 or fp(t[p]) not in t]
print("roots:", [(nm(r), r) for r in roots])
# find metarig pid
met=[p for p in t if nm(p)=="metarig"]
print("metarig pids:", met)
mr=met[0]
out=[]
def walk(pid,prefix):
    n=nm(pid); p=n if prefix=="" else prefix+"/"+n
    out.append((p, t[pid]))
    for c in kids.get(pid,[]): walk(c,p)
walk(mr,"")
print("nodes under metarig:", len(out))
rec={}
for p,tt in out:
    r=tt["m_LocalRotation"]; pos=tt["m_LocalPosition"]; s=tt["m_LocalScale"]
    crc=zlib.crc32(p.encode())&0xffffffff
    rec[p]={"crc":crc,"rot":[r["x"],r["y"],r["z"],r["w"]],"pos":[pos["x"],pos["y"],pos["z"]],"scl":[s["x"],s["y"],s["z"]]}
    print(f"  {p:46s} crc={crc:10d} rot=({r['x']:.4f},{r['y']:.4f},{r['z']:.4f},{r['w']:.4f}) pos=({pos['x']:.3f},{pos['y']:.3f},{pos['z']:.3f})")
fixtures={"spine":2463100623,"spine/spine.001":254801669,"spine/spine.001/spine.002":4095213152,
          "spine/spine.001/spine.002/spine.003":674009458,"spine/thigh.L":49267413,"spine/thigh.R":4175495094}
print("=== fixtures ===")
ok=True
for p,exp in fixtures.items():
    got=zlib.crc32(p.encode())&0xffffffff
    print(f"  {p:42s} expect={exp:10d} got={got:10d} {'OK' if got==exp else 'MISMATCH'}")
    ok = ok and got==exp
print("fixtures ok:", ok, "| paths present:", all(p in rec for p in fixtures))
json.dump(rec, open("metarig_rest.json","w"), indent=1)
