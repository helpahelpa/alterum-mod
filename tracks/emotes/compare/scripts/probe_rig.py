import json, math, os
import UnityPy
HERE=os.path.dirname(os.path.abspath(__file__))
D="/mnt/c/Program Files (x86)/Steam/steamapps/common/Lethal Company/Lethal Company_Data"
def qmul(a,b):
    ax,ay,az,aw=a; bx,by,bz,bw=b
    return (aw*bx+ax*bw+ay*bz-az*by, aw*by-ax*bz+ay*bw+az*bx, aw*bz+ax*by-ay*bx+az*bw, aw*bw-ax*bx-ay*by-az*bz)
def qrot(q,v):
    x,y,z,w=q; vx,vy,vz=v
    tx=2*(y*vz-z*vy); ty=2*(z*vx-x*vz); tz=2*(x*vy-y*vx)
    return (vx+w*tx+(y*tz-z*ty), vy+w*ty+(z*tx-x*tz), vz+w*tz+(x*ty-y*tx))
def world(doc, rootscale):
    out={}
    for p in doc["order"]:
        if p==doc["root"]:
            pp,pr,ps=(0,0,0),(0,0,0,1),rootscale
        else:
            pp,pr,ps=out[p.rsplit("/",1)[0]] if "/" in p else out[doc["root"]]
        b=doc["bones"][p]; lr=tuple(b["rot"]); lp=tuple(b["pos"]); ls=b["scl"][0]
        wr=qmul(pr,lr); rp=qrot(pr,tuple(c*ps for c in lp))
        out[p]=((pp[0]+rp[0],pp[1]+rp[1],pp[2]+rp[2]),wr,ps*ls)
    return out
real=json.load(open(os.path.join(HERE,"..","lc_rig.json")))
oldraw=json.load(open(os.path.join(HERE,"..","..","metarig_rest.json")))
old={"bones":{("metarig" if k=="" else k):v for k,v in oldraw.items()},"root":"metarig",
     "order":sorted([("metarig" if k=="" else k) for k in oldraw],key=lambda q:(q.count("/"),q))}
rw=world(real,0.5608136); ow=world(old,0.5608136)
print(f"{'bone':46s} {'real world (m, model)':34s} {'bundle world':34s}")
for k in ["metarig/spine","metarig/spine/spine.001/spine.002/spine.003/spine.004",
          "metarig/spine/spine.001/spine.002/spine.003/shoulder.L",
          "metarig/spine/spine.001/spine.002/spine.003/shoulder.L/arm.L_upper",
          "metarig/spine/spine.001/spine.002/spine.003/shoulder.L/arm.L_upper/arm.L_lower",
          "metarig/spine/spine.001/spine.002/spine.003/shoulder.L/arm.L_upper/arm.L_lower/hand.L",
          "metarig/spine/thigh.L","metarig/spine/thigh.L/shin.L","metarig/spine/thigh.L/shin.L/foot.L",
          "metarig/spine/thigh.L/shin.L/foot.L/toe.L"]:
    a=rw[k][0]; b=ow[k][0]
    print(f"{k.split('/')[-1]:46s} ({a[0]:6.3f},{a[1]:6.3f},{a[2]:6.3f})            ({b[0]:6.3f},{b[1]:6.3f},{b[2]:6.3f})")
print("\nworld segment dirs (model space):")
for a,b in [("metarig/spine/thigh.L","metarig/spine/thigh.L/shin.L"),
            ("metarig/spine/thigh.L/shin.L","metarig/spine/thigh.L/shin.L/foot.L"),
            ("metarig/spine/spine.001/spine.002/spine.003/shoulder.L","metarig/spine/spine.001/spine.002/spine.003/shoulder.L/arm.L_upper"),
            ("metarig/spine/spine.001/spine.002/spine.003/shoulder.L/arm.L_upper","metarig/spine/spine.001/spine.002/spine.003/shoulder.L/arm.L_upper/arm.L_lower"),
            ("metarig/spine/spine.001/spine.002/spine.003/shoulder.L/arm.L_upper/arm.L_lower","metarig/spine/spine.001/spine.002/spine.003/shoulder.L/arm.L_upper/arm.L_lower/hand.L")]:
    va=[rw[b][0][i]-rw[a][0][i] for i in range(3)]; n=math.dist(a and rw[a][0],rw[b][0]); va=[c/n for c in va]
    vb=[ow[b][0][i]-ow[a][0][i] for i in range(3)]; n2=math.dist(ow[a][0],ow[b][0]); vb=[c/n2 for c in vb]
    ang=math.degrees(math.acos(max(-1,min(1,sum(x*y for x,y in zip(va,vb))))))
    print(f"{a.split('/')[-1]:18s}->{b.split('/')[-1]:18s} real=({va[0]:6.2f},{va[1]:6.2f},{va[2]:6.2f}) bundle=({vb[0]:6.2f},{vb[1]:6.2f},{vb[2]:6.2f}) angle={ang:6.1f}deg len real={n:.3f} bundle={n2:.3f}")
# bind pose compare
smr=real["smr"]
for s in smr:
    print("\nSMR",s["name"],"bones",len(s["bones"]))
    if not s["bones"]: continue
    # find pelvis index
    bi={p:i for i,p in enumerate(s["bones"])}
    pel="metarig/spine"
    if pel not in bi: continue
    def bp_pos(name):
        i=bi[name]; m=s["bindpose"][i]
        # matrix is worldToBone, invert affine
        import numpy as np
        M=np.array(m,dtype=float).reshape(4,4)  # Unity serialized e00,e01..? row-major
        inv=np.linalg.inv(M)
        return inv[:3,3], inv
    try:
        pp,_=bp_pos(pel)
        for nm2 in ["metarig/spine/spine.001/spine.002/spine.003/shoulder.L/arm.L_upper",
                    "metarig/spine/spine.001/spine.002/spine.003/shoulder.L/arm.L_upper/arm.L_lower",
                    "metarig/spine/spine.001/spine.002/spine.003/shoulder.L/arm.L_upper/arm.L_lower/hand.L",
                    "metarig/spine/thigh.L/shin.L/foot.L"]:
            if nm2 not in bi: continue
            q,_=bp_pos(nm2)
            d=(q-pp)
            print("  bind",nm2.split('/')[-1],"pelvis-rel",[round(float(x),3) for x in d])
    except Exception as e:
        print("  bind err",e)
