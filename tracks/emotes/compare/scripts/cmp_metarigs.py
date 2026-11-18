import os, math
import UnityPy
D="/mnt/c/Program Files (x86)/Steam/steamapps/common/Lethal Company/Lethal Company_Data"
p=os.path.join(D,"sharedassets3.assets")
env=UnityPy.load(p)
g={};t={}
for o in env.objects:
    if o.type.name=="GameObject": g[o.path_id]=o.read_typetree()
    elif o.type.name in ("Transform","RectTransform"): t[o.path_id]=o.read_typetree()
def gid(tt,k):
    v=tt.get(k); return v.get("m_PathID",0) if isinstance(v,dict) else 0
def nm(pid): return g.get(gid(t[pid],"m_GameObject"),{}).get("m_Name","?")
kids={}
for pid,tt in t.items(): kids.setdefault(gid(tt,"m_Father"),[]).append(pid)
def full(pid):
    ch=[];c=pid
    while c:
        ch.append(nm(c)); f=gid(t[c],"m_Father")
        if f not in t: break
        c=f
    return "/".join(reversed(ch))
def qmul(a,b):
    ax,ay,az,aw=a; bx,by,bz,bw=b
    return (aw*bx+ax*bw+ay*bz-az*by, aw*by-ax*bz+ay*bw+az*bx, aw*bz+ax*by-ay*bx+az*bw, aw*bw-ax*bx-ay*by-az*bz)
def qrot(q,v):
    x,y,z,w=q; vx,vy,vz=v
    tx=2*(y*vz-z*vy); ty=2*(z*vx-x*vz); tz=2*(x*vy-y*vx)
    return (vx+w*tx+(y*tz-z*ty), vy+w*ty+(z*tx-x*tz), vz+w*tz+(x*ty-y*tx))
metarigs=[pid for pid in t if nm(pid)=="metarig"]
print("metarig roots:",[(pid,full(pid)) for pid in metarigs])
for root in metarigs:
    fp=full(root)
    if not any(k in fp for k in ("Player/","ChangableSuit","PlayerRagdollFlowers","PlayerRagdoll","ScavengerModel")): continue
    bones={};order=[]
    stack=[(root,"",(0,0,0),(0,0,0,1),1.0)]
    worlds={}
    while stack:
        pid,prefix,pp,pr,ps=stack.pop()
        n=nm(pid); path=n if not prefix else prefix+"/"+n
        tt=t[pid]; lr=(tt["m_LocalRotation"]["x"],tt["m_LocalRotation"]["y"],tt["m_LocalRotation"]["z"],tt["m_LocalRotation"]["w"])
        lp=(tt["m_LocalPosition"]["x"],tt["m_LocalPosition"]["y"],tt["m_LocalPosition"]["z"])
        wr=qmul(pr,lr); rp=qrot(pr,tuple(c*ps for c in lp)); wp=(pp[0]+rp[0],pp[1]+rp[1],pp[2]+rp[2])
        worlds[path]=wp
        for c in kids.get(pid,[]): stack.append((c,path,wp,wr,ps*tt["m_LocalScale"]["x"]))
    def rel(a,b):
        if a not in worlds or b not in worlds: return None
        v=[worlds[b][i]-worlds[a][i] for i in range(3)]; n=math.sqrt(sum(c*c for c in v))
        return (v[0]/n,v[1]/n,v[2]/n) if n>1e-9 else None
    def dirs():
        base="metarig/spine/spine.001/spine.002/spine.003/shoulder.L"
        return {"thighL":rel("metarig/spine/thigh.L","metarig/spine/thigh.L/shin.L"),
                "shinL":rel("metarig/spine/thigh.L/shin.L","metarig/spine/thigh.L/shin.L/foot.L"),
                "upL":rel(base,base+"/arm.L_upper"),
                "loL":rel(base+"/arm.L_upper",base+"/arm.L_upper/arm.L_lower"),
                "handL":rel(base+"/arm.L_upper/arm.L_lower",base+"/arm.L_upper/arm.L_lower/hand.L")}
    print("\n##",fp)
    for k,v in dirs().items():
        print(f"   {k:7s}", None if v is None else "(%5.2f,%5.2f,%5.2f)"%v)
