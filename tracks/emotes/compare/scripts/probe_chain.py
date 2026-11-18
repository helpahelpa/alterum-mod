import sys, os, json
import UnityPy
D="/mnt/c/Program Files (x86)/Steam/steamapps/common/Lethal Company/Lethal Company_Data"
def gid(tt,key):
    v=tt.get(key); return v.get("m_PathID",0) if isinstance(v,dict) else 0
p=sys.argv[1] if os.path.exists(sys.argv[1]) else os.path.join(D,sys.argv[1])
env=UnityPy.load(p)
g={};t={};mb={};ms={}
for o in env.objects:
    n=o.type.name
    if n=="GameObject": g[o.path_id]=o.read_typetree()
    elif n in ("Transform","RectTransform"): t[o.path_id]=o.read_typetree()
    elif n=="MonoBehaviour": mb[o.path_id]=o
    elif n=="MonoScript": ms[o.path_id]=o.read_typetree()
def nm(pid): return g.get(gid(t[pid],"m_GameObject"),{}).get("m_Name","?")
# transforms under Player/ScavengerModel whose name is Rig 1/LeftArm etc
targets={}
for pid in t:
    chain=[]; c=pid
    while c:
        chain.append(nm(c)); f=gid(t[c],"m_Father")
        if f not in t: break
        c=f
    full="/".join(reversed(chain))
    if full.endswith("metarig/Rig 1/LeftArm") or full.endswith("metarig/Rig 1/RightArm"):
        targets[pid]=full
print("targets:",targets)
# component lists on the GameObject
comp={}
for o in env.objects:
    if o.type.name in ("MonoBehaviour","Transform","CapsuleCollider","BoxCollider","Animator"):
        try: tt=o.read_typetree()
        except Exception: continue
        go=gid(tt,"m_GameObject")
        if go: comp.setdefault(go,[]).append((o.type.name,o.path_id))
for pid,full in targets.items():
    go=gid(t[pid],"m_GameObject")
    print("##",full,"GO",go)
    for typ,opid in comp.get(go,[]):
        print("  ",typ,opid)
        if typ=="MonoBehaviour":
            o=mb[opid]; tt=o.read_typetree()
            script=gid(tt,"m_Script")
            s=ms.get(script,{})
            print("     script",s.get("m_ClassName"),s.get("m_Namespace"),s.get("m_AssemblyName"))
            for k,v in tt.items():
                if k in ("m_GameObject","m_Script","m_Enabled","m_EditorHideFlags","m_EditorClassIdentifier"): continue
                def fmt(x):
                    if isinstance(x,dict):
                        if "m_PathID" in x: return "PPtr(%d)"%x["m_PathID"]
                        return {kk:(round(vv,4) if isinstance(vv,float) else vv) for kk,vv in x.items()}
                    return x
                print("     ",k,"=",fmt(v))
