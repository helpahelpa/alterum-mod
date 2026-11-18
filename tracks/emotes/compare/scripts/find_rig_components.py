import os, binascii
import UnityPy
D="/mnt/c/Program Files (x86)/Steam/steamapps/common/Lethal Company/Lethal Company_Data"
path=os.path.join(D,"sharedassets3.assets")
env=UnityPy.load(path)
g={};t={};mbs=[]
for o in env.objects:
    n=o.type.name
    if n=="GameObject": tt=o.read_typetree(); g[o.path_id]=tt
    elif n in ("Transform","RectTransform"): tt=o.read_typetree(); t[o.path_id]=tt
    elif n=="MonoBehaviour": mbs.append(o)
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
# map transform path -> pid
pid_of={full(p):p for p in t}
print("mb count",len(mbs))
# inspect a MonoBehaviour read
for o in mbs[:3]:
    try:
        ob=o.read()
        print("read ok",o.path_id,type(ob),getattr(ob,'m_Script',None))
    except Exception as e:
        print("read fail",o.path_id,e)
# raw reader: parse m_GameObject and m_Script from bytes? use typetree with check_read false?
for o in mbs:
    try:
        tt=o.read_typetree(check_read=False)
    except Exception as e:
        continue
    go=gid(tt,"m_GameObject")
    if go in t and ("ScavengerModel" in full(t[go] if go in t else 0) or False):
        pass
