import sys, os, json
import UnityPy

def gid(tt, key):
    v=tt.get(key)
    return v.get("m_PathID",0) if isinstance(v,dict) else 0

def load(path):
    env=UnityPy.load(path)
    g={}; t={}; smr=[]; mr=[]
    for o in env.objects:
        if o.type.name=="GameObject":
            tt=o.read_typetree(); g[o.path_id]=tt
        elif o.type.name in ("Transform","RectTransform"):
            tt=o.read_typetree(); t[o.path_id]=tt
        elif o.type.name=="SkinnedMeshRenderer":
            smr.append(o.read_typetree())
    return env,g,t,smr

def tree(g,t,root_pid):
    kids={}
    for pid,tt in t.items(): kids.setdefault(gid(tt,"m_Father"),[]).append(pid)
    out=[]
    def name(pid):
        return g.get(gid(t[pid],"m_GameObject"),{}).get("m_Name","?")
    stack=[(root_pid,"",0)]
    while stack:
        pid,path,d=stack.pop()
        n=name(pid)
        p=n if d==0 else path+"/"+n
        tt=t[pid]
        r=tt["m_LocalRotation"]; pos=tt["m_LocalPosition"]; s=tt["m_LocalScale"]
        out.append((d,p,[r["x"],r["y"],r["z"],r["w"]],[pos["x"],pos["y"],pos["z"]],[s["x"],s["y"],s["z"]]))
        for c in kids.get(pid,[]): stack.append((c,p,d+1))
    return out

D="/mnt/c/Program Files (x86)/Steam/steamapps/common/Lethal Company/Lethal Company_Data"
for fn in sys.argv[1:]:
    path=fn if os.path.exists(fn) else os.path.join(D,fn)
    env,g,t,smr=load(path)
    print("==",os.path.basename(path),"transforms",len(t),"smr",len(smr))
    # top-level transforms (no father) whose subtree contains a metarig
    names={pid: g.get(gid(tt,"m_GameObject"),{}).get("m_Name","?") for pid,tt in t.items()}
    met=[pid for pid,n in names.items() if n=="metarig"]
    print("  metarig transforms:",len(met))
    for m in met:
        # walk to root
        pid=m
        while gid(t[pid],"m_Father") in t: pid=gid(t[pid],"m_Father")
        tr=tree(g,t,pid)
        print("  root:",names[pid],"nodes:",len(tr))
        for d,p,r,pos,s in tr[:400]:
            if d<=4 or 'metarig' in p:
                print("    "+"  "*d+p, ["%.3f"%x for x in pos])
        for r in smr:
            go=gid(r,"m_GameObject")
            print("    SMR", g.get(go,{}).get("m_Name","?"), "bones", len(r.get("m_Bones",[])), "rootBone", gid(r,"m_RootBone"))
