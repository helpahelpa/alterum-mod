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
print("transforms",len(t))
# find all ScavengerModel transforms and their ancestry
for pid in t:
    if nm(pid)=="ScavengerModel":
        chain=[]; c=pid
        while c:
            chain.append((c,nm(c)))
            f=gid(t[c],"m_Father")
            if f not in t: break
            c=f
        print("SM pid",pid,"ancestry(root-first):",[n for _,n in reversed(chain)])
# print children of the ScavengerModel whose ancestry is a bare root (no parent)
kids={}
for pid,tt in t.items(): kids.setdefault(gid(tt,"m_Father"),[]).append(pid)
for pid in t:
    if nm(pid)=="ScavengerModel" and gid(t[pid],"m_Father")==0:
        print("top-level ScavengerModel pid",pid,"children:",[nm(c) for c in kids[pid]])
        # direct metarig child
        for c in kids[pid]:
            if nm(c)=="metarig":
                print(" metarig pid",c,"pos",t[c]["m_LocalPosition"])
                for cc in kids[c][:8]: print("   child",nm(cc),t[cc]["m_LocalPosition"])
                print("   nchildren",len(kids[c]))
                # find spine child
                for cc in kids[c]:
                    if nm(cc)=="spine":
                        print("   spine pid",cc,"pos",t[cc]["m_LocalPosition"])
                        for ccc in kids[cc]: print("     ",nm(ccc),t[ccc]["m_LocalPosition"])
