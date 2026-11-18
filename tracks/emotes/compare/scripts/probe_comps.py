import sys, os
import UnityPy
D="/mnt/c/Program Files (x86)/Steam/steamapps/common/Lethal Company/Lethal Company_Data"
def gid(tt,key):
    v=tt.get(key); return v.get("m_PathID",0) if isinstance(v,dict) else 0
p=sys.argv[1] if os.path.exists(sys.argv[1]) else os.path.join(D,sys.argv[1])
env=UnityPy.load(p)
g={};t={}
for o in env.objects:
    n=o.type.name
    if n=="GameObject": g[o.path_id]=o.read_typetree()
    elif n in ("Transform","RectTransform"): t[o.path_id]=o.read_typetree()
def nm(pid): return g.get(gid(t[pid],"m_GameObject"),{}).get("m_Name","?")
# find metarig transform under Player/ScavengerModel
want={}
for pid in t:
    chain=[]; c=pid
    while c:
        chain.append(nm(c)); f=gid(t[c],"m_Father")
        if f not in t: break
        c=f
    full="/".join(reversed(chain))
    for key in ("Player/ScavengerModel/metarig", "Player/ScavengerModel/metarig/Rig 1"):
        if full==key: want[pid]=key
print(want)
compgo={}
for o in env.objects:
    n=o.type.name
    if n in ("Transform","RectTransform"): continue
    try: tt=o.read_typetree()
    except Exception as e: tt=None
    go=None
    if tt:
        go=gid(tt,"m_GameObject")
    else:
        # find GameObject link by reading raw? try o.read()
        try:
            ob=o.read()
            go=getattr(ob,'m_GameObject',None)
            if go is not None: go=go.path_id
        except Exception: pass
    if go: compgo.setdefault(go,[]).append((n,o.path_id,tt is not None))
for pid,key in want.items():
    go=gid(t[pid],"m_GameObject")
    print("##",key,"GO",go)
    for n,opid,ok in compgo.get(go,[]): print("   ",n,opid,"typetree" if ok else "no-typetree")
