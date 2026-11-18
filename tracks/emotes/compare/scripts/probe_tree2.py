import sys, os
import UnityPy
D="/mnt/c/Program Files (x86)/Steam/steamapps/common/Lethal Company/Lethal Company_Data"
def gid(tt,key):
    v=tt.get(key); return v.get("m_PathID",0) if isinstance(v,dict) else 0
p=sys.argv[1] if os.path.exists(sys.argv[1]) else os.path.join(D,sys.argv[1])
env=UnityPy.load(p)
g={};t={}
for o in env.objects:
    if o.type.name=="GameObject": g[o.path_id]=o.read_typetree()
    elif o.type.name in ("Transform","RectTransform"): t[o.path_id]=o.read_typetree()
def nm(pid): return g.get(gid(t[pid],"m_GameObject"),{}).get("m_Name","?")
for target in ("spine","spine.001","arm.L_upper","LeftArm_target","Rig 1"):
    print("###",target)
    for pid in t:
        if nm(pid)!=target: continue
        chain=[]; c=pid
        while c:
            chain.append(nm(c)); f=gid(t[c],"m_Father")
            if f not in t: break
            c=f
        print("  pid",pid,"scale",t[pid]["m_LocalScale"]["x"],"pos",tuple(round(v,4) for v in t[pid]["m_LocalPosition"].values()))
        print("    ", "/".join(reversed(chain)))
