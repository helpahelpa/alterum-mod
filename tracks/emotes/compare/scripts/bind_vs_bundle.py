import json, math, os
import numpy as np
HERE=os.path.dirname(os.path.abspath(__file__))
bind=json.load(open(os.path.join(HERE,"..","lc_bind.json")))
oldraw=json.load(open(os.path.join(HERE,"..","..","metarig_rest.json")))
old={("metarig" if k=="" else k):v for k,v in oldraw.items()}
# bind pos are pelvis-relative positions (mesh space); rot are mesh-space world rotations.
# Reconstruct bind local TRS by walking the old hierarchy (same parent relations) using
# bind positions and rotations: local = inv(parentWorld) * world.
def qmul(a,b):
    ax,ay,az,aw=a; bx,by,bz,bw=b
    return (aw*bx+ax*bw+ay*bz-az*by, aw*by-ax*bz+ay*bw+az*bx, aw*bz+ax*by-ay*bx+az*bw, aw*bw-ax*bx-ay*by-az*bz)
def qconj(q): return (-q[0],-q[1],-q[2],q[3])
def qnorm(q):
    n=math.sqrt(sum(c*c for c in q)); return tuple(c/n for c in q) if n>1e-12 else (0,0,0,1)
def qrot(q,v):
    x,y,z,w=q; vx,vy,vz=v
    tx=2*(y*vz-z*vy); ty=2*(z*vx-x*vz); tz=2*(x*vy-y*vx)
    return (vx+w*tx+(y*tz-z*ty), vy+w*ty+(z*tx-x*tz), vz+w*tz+(x*ty-y*tx))
# build world for bind via bind.json: pos already pelvis-relative. Need the pelvis absolute = 0.
# parent relations from old (bundle) hierarchy
parent={}
for p in old:
    if p=="metarig": parent[p]=None
    else: parent[p]=p.rsplit("/",1)[0] if "/" in p else "metarig"
# bind world: position = bind.pos + pelvisAbs(0); rotation = bind.rot
bw={}
for p,v in bind["bones"].items():
    bw[p]=(tuple(v["pos"]),tuple(v["rot"]))
# bundle world using its own locals
def world_old():
    out={}
    order=sorted(old,key=lambda q:(q.count("/"),q))
    for p in order:
        if p=="metarig":
            pp,pr=(0,0,0),(0,0,0,1)
        else:
            pp,pr=out[parent[p]]
        b=old[p]; lr=tuple(b["rot"]); lp=tuple(b["pos"])
        wr=qmul(pr,lr); rp=qrot(pr,lp)
        out[p]=((pp[0]+rp[0],pp[1]+rp[1],pp[2]+rp[2]),wr)
    return out
ow=world_old()
# local rotations of bind
def local_of(p):
    if p not in bw: return None
    pp=parent[p]
    if pp is None or pp not in bw: return bw[p][1]
    pr=bw[pp][1]
    return qnorm(qmul(qconj(pr),bw[p][1]))
# Compare *local* rotations and *local segment* directions
print("bone".ljust(64),"local rot diff vs bundle (deg)","bind segment dir vs bundle (deg)")
for p in sorted(bind["bones"],key=lambda q:(q.count("/"),q)):
    if p not in old: continue
    lb=local_of(p); lo=tuple(old[p]["rot"])
    dq=2*math.degrees(math.acos(min(1,abs(sum(x*y for x,y in zip(lb,lo))))))
    # segment bind vs bundle: child dir
    line=""
    child=p+"/"+"?" 
    # choose the next bone in old hierarchy
    child=None
    for q in old:
        if parent.get(q)==p: child=q; break
    if child and child in bw:
        a=np.array(bw[p][0]); b=np.array(bw[child][0])
        da=b-a; da=da/np.linalg.norm(da)
        a2=np.array(ow[p][0]); b2=np.array(ow[child][0])
        db=b2-a2; db=db/np.linalg.norm(db)
        ang=math.degrees(math.acos(max(-1,min(1,float(np.dot(da,db))))))
        line=f"{ang:7.2f}"
    print(p.ljust(64), f"{dq:7.2f}", line)
