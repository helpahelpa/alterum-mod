import os, sys, json
import UnityPy
D="/mnt/c/Program Files (x86)/Steam/steamapps/common/Lethal Company/Lethal Company_Data"
p=os.path.join(D,"sharedassets3.assets")
env=UnityPy.load(p)
g={};t={};sm=[]
for o in env.objects:
    n=o.type.name
    if n=="GameObject": g[o.path_id]=o.read_typetree()
    elif n in ("Transform","RectTransform"): t[o.path_id]=o.read_typetree()
    elif n=="SkinnedMeshRenderer": sm.append((o.path_id,o.read_typetree()))
def gid(tt,k):
    v=tt.get(k); return v.get("m_PathID",0) if isinstance(v,dict) else 0
def nm(pid): return g.get(gid(t[pid],"m_GameObject"),{}).get("m_Name","?")
def full(pid):
    ch=[];c=pid
    while c:
        ch.append(nm(c)); f=gid(t[c],"m_Father")
        if f not in t: break
        c=f
    return "/".join(reversed(ch))
for pid,tt in sm:
    go=gid(tt,"m_GameObject")
    if go not in g: continue
    gopath_pid=next((k for k,v in t.items() if gid(v,"m_GameObject")==go),0)
    gpath=full(gopath_pid) if gopath_pid else "?"
    if "Player" not in gpath: continue
    bones=tt.get("m_Bones") or []
    bp=[full(gid(b,"m_PathID")) if gid(b,"m_PathID") in t else None for b in bones[:4]]
    rb=gid(tt,"m_RootBone")
    print(pid,tt and g.get(go,{}).get("m_Name"),"gopath",gpath,"nbones",len(bones),"rootBone",(full(rb) if rb in t else rb))
    print("   bones[:4]",bp)
    print("   bindpose",len(tt.get("m_BindPose") or []))
