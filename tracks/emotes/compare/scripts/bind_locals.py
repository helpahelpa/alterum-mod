import json, math, os
import numpy as np
HERE=os.path.dirname(os.path.abspath(__file__))
bind=json.load(open(os.path.join(HERE,"..","lc_bind.json")))
real=json.load(open(os.path.join(HERE,"..","lc_rig.json")))
oldraw=json.load(open(os.path.join(HERE,"..","..","metarig_rest.json")))
old={("metarig" if k=="" else k):v for k,v in oldraw.items()}
parent={}
for p in old:
    if p=="metarig": parent[p]=None
    else: parent[p]=p.rsplit("/",1)[0] if "/" in p else "metarig"
def qconj(q): return (-q[0],-q[1],-q[2],q[3])
def qnorm(q):
    n=math.sqrt(sum(c*c for c in q)); return tuple(c/n for c in q) if n>1e-12 else (0,0,0,1)
def qrot(q,v):
    x,y,z,w=q; vx,vy,vz=v
    tx=2*(y*vz-z*vy); ty=2*(z*vx-x*vz); tz=2*(x*vy-y*vx)
    return (vx+w*tx+(y*tz-z*ty), vy+w*ty+(z*tx-x*tz), vz+w*tz+(x*ty-y*tx))
def qmul(a,b):
    ax,ay,az,aw=a; bx,by,bz,bw=b
    return (aw*bx+ax*bw+ay*bz-az*by, aw*by-ax*bz+ay*bw+az*bx, aw*bz+ax*by-ay*bx+az*bw, aw*bw-ax*bx-ay*by-az*bz)
def qinv(q):
    n=sum(c*c for c in q); return (-q[0]/n,-q[1]/n,-q[2]/n,q[3]/n)
# bind world: pelvis at origin
bw={p:(np.array(v["pos"]),np.array(v["rot"])) for p,v in bind["bones"].items()}
# reconstruct local pos and rot
rows=[]
for p,(pos,rot) in bw.items():
    pp=parent.get(p)
    if pp in bw:
        prow,prot=bw[pp]
        lp=qrot(qinv(tuple(prot)), tuple(pos-prow))
        lr=qnorm(qmul(qinv(tuple(prot)),tuple(rot)))
    else:
        lp=tuple(pos); lr=tuple(rot)
    rows.append((p,lp,lr))
print(f"{'bone':60s} {'bind local pos':28s} {'bundle local pos':28s} {'real prefab local pos':28s}")
for p,lp,lr in rows:
    o=old.get(p); r=real["bones"].get(p)
    ob=" ".join("%6.3f"%x for x in o["pos"]) if o else "-"
    rb=" ".join("%6.3f"%x for x in r["pos"]) if r else "-"
    print(f"{p.split('metarig/')[-1]:60s} ({lp[0]:6.3f},{lp[1]:6.3f},{lp[2]:6.3f})    {ob}    {rb}")
