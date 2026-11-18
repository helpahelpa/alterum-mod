import json, math, os
HERE=os.path.dirname(os.path.abspath(__file__))
def qmul(a,b):
    ax,ay,az,aw=a; bx,by,bz,bw=b
    return (aw*bx+ax*bw+ay*bz-az*by, aw*by-ax*bz+ay*bw+az*bx, aw*bz+ax*by-ay*bx+az*bw, aw*bw-ax*bx-ay*by-az*bz)
def qrot(q,v):
    x,y,z,w=q; vx,vy,vz=v
    tx=2*(y*vz-z*vy); ty=2*(z*vx-x*vz); tz=2*(x*vy-y*vx)
    return (vx+w*tx+(y*tz-z*ty), vy+w*ty+(z*tx-x*tz), vz+w*tz+(x*ty-y*tx))
real=json.load(open(os.path.join(HERE,"..","lc_rig.json")))
oldraw=json.load(open(os.path.join(HERE,"..","..","metarig_rest.json")))
old={"bones":{("metarig" if k=="" else k):v for k,v in oldraw.items()},
     "root":"metarig","order":sorted([("metarig" if k=="" else k) for k in oldraw], key=lambda q:(q.count("/"),q))}
def world(doc, rootscale=1.0):
    out={}; rootpos=(0,0,0); rootrot=(0,0,0,1)
    for p in doc["order"]:
        if p==doc["root"]:
            pp,pr,ps=rootpos,rootrot,rootscale
        else:
            pp,pr,ps=out[p.rsplit("/",1)[0]] if "/" in p else out[doc["root"]]
        b=doc["bones"][p]
        lr=tuple(b["rot"]); lp=tuple(b["pos"]); ls=b["scl"][0]
        wr=qmul(pr,lr)
        rp=qrot(pr,tuple(c*ps for c in lp))
        out[p]=((pp[0]+rp[0],pp[1]+rp[1],pp[2]+rp[2]),wr,ps*ls)
    return out
rw=world(real,0.5608136); ow=world(old,0.5608136)
print("bone".ljust(64),"world dpos cm  dWorldRot deg")
for k in sorted(real["bones"], key=lambda q:(q.count("/"),q)):
    if k not in old["bones"]: continue
    a,b=rw[k],ow[k]
    dp=math.dist(a[0],b[0])*100
    dq=2*math.degrees(math.acos(min(1,abs(sum(x*y for x,y in zip(a[1],b[1]))))))
    if dp>0.3 or dq>3:
        print(k.ljust(64), f"{dp:8.2f} {dq:8.2f}")
