import sys, os, json
import UnityPy
D="/mnt/c/Program Files (x86)/Steam/steamapps/common/Lethal Company/Lethal Company_Data"
def gid(tt,key):
    v=tt.get(key); return v.get("m_PathID",0) if isinstance(v,dict) else 0
def load(path):
    env=UnityPy.load(path); g={}; t={}; smr={}
    for o in env.objects:
        n=o.type.name
        if n=="GameObject": tt=o.read_typetree(); g[o.path_id]=tt
        elif n in ("Transform","RectTransform"): tt=o.read_typetree(); t[o.path_id]=tt
        elif n=="SkinnedMeshRenderer": tt=o.read_typetree(); smr[o.path_id]=tt
    return env,g,t,smr
def dump(path, rootname):
    env,g,t,smr=load(path)
    names={pid: g.get(gid(tt,"m_GameObject"),{}).get("m_Name","?") for pid,tt in t.items()}
    roots=[pid for pid,n in names.items() if n==rootname]
    for r in roots:
        print("ROOT",rootname,"pid",r,"localpos",t[r]["m_LocalPosition"],"rot",t[r]["m_LocalRotation"],"scale",t[r]["m_LocalScale"])
        kids={}
        for pid,tt in t.items(): kids.setdefault(gid(tt,"m_Father"),[]).append(pid)
        stack=[(r,0,"")]
        n=0
        while stack:
            pid,d,pre=stack.pop(0)
            nm=names[pid]; p=(pre+"/"+nm) if pre else nm
            tt=t[pid]; pos=tt["m_LocalPosition"]
            print("  "*d + nm, "  pos=(%.4f,%.4f,%.4f)"%(pos["x"],pos["y"],pos["z"]), "rot=(%.4f,%.4f,%.4f,%.4f)"%(tt["m_LocalRotation"]["x"],tt["m_LocalRotation"]["y"],tt["m_LocalRotation"]["z"],tt["m_LocalRotation"]["w"]), "scl=%.3f"%tt["m_LocalScale"]["x"])
            for c in sorted(kids.get(pid,[])): stack.append((c,d+1,p))
        # smr under this root
        sub=set()
        # collect via names prefix unsafe; do another walk
        st=[r]
        while st:
            x=st.pop(); sub.add(x); st+=kids.get(x,[])
        for pid,tt in smr.items():
            if gid(tt,"m_GameObject") in sub or gid(tt,"m_RootBone") in sub:
                print("  SMR", g.get(gid(tt,"m_GameObject"),{}).get("m_Name","?"),"bones",len(tt.get("m_Bones",[])),"rootBone",gid(tt,"m_RootBone"),"bindpose",len(tt.get("m_VertexBuffer") or []))
if __name__=="__main__":
    for fn in sys.argv[1:]:
        p=fn if os.path.exists(fn) else os.path.join(D,fn)
        print("=====",fn)
        dump(p,"ScavengerModel")
