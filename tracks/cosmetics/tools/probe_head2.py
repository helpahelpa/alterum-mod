import UnityPy, collections, math
from UnityPy.helpers.MeshHelper import MeshHandler
env = UnityPy.load("unpack_bundle_70132")
objs = {o.path_id: o for o in env.objects}
tt = {}
for pid,o in objs.items():
    try: tt[pid] = o.read_typetree()
    except Exception: tt[pid] = None

transforms = {pid:d for pid,d in tt.items() if objs[pid].type.name in ("Transform","RectTransform")}
gos = {pid:d for pid,d in tt.items() if objs[pid].type.name=="GameObject"}

def tr_name(pid):
    g = transforms[pid]["m_GameObject"]["m_PathID"]
    return gos[g]["m_Name"]

# find metarig
metarig = None
for pid,d in transforms.items():
    if tr_name(pid) == "metarig": metarig = pid
print("metarig", metarig, "scale", transforms[metarig]["m_LocalScale"])
# path to root
def chain(pid):
    out=[]
    while pid:
        out.append((pid, tr_name(pid)))
        f = transforms[pid].get("m_Father") or {}
        pid = f.get("m_PathID", 0)
    return out
print("metarig chain:", [n for _,n in chain(metarig)])
# find spine.004
head = None
for pid,d in transforms.items():
    if tr_name(pid) == "spine.004": head = pid
print("head transform", head)
print("head chain:", [n for _,n in chain(head)])
def qmul(a,b):
    ax,ay,az,aw=a; bx,by,bz,bw=b
    return (aw*bx+ax*bw+ay*bz-az*by, aw*by-ax*bz+ay*bw+az*bx, aw*bz+ax*by-ay*bx+az*bw, aw*bw-ax*bx-ay*by-az*bz)
def qrot(q,v):
    x,y,z,w=q; vx,vy,vz=v
    # q * v * q^-1
    tx = 2*(y*vz - z*vy); ty = 2*(z*vx - x*vz); tz = 2*(x*vy - y*vx)
    return (vx + w*tx + (y*tz - z*ty), vy + w*ty + (z*tx - x*tz), vz + w*tz + (x*ty - y*tx))
def world(pid):
    pos=(0,0,0); rot=(0,0,0,1); scl=(1,1,1)
    for p,_ in chain(pid):
        d=transforms[p]
        lp=d["m_LocalPosition"]; lr=d["m_LocalRotation"]; ls=d["m_LocalScale"]
        pv=(lp["x"],lp["y"],lp["z"]); pr=(lr["x"],lr["y"],lr["z"],lr["w"]); ps=(ls["x"],ls["y"],ls["z"])
        pv=(pv[0]*scl[0], pv[1]*scl[1], pv[2]*scl[2])
        rv=qrot(rot,pv)
        pos=(pos[0]+rv[0], pos[1]+rv[1], pos[2]+rv[2])
        rot=qmul(rot,pr)
        scl=(scl[0]*ps[0], scl[1]*ps[1], scl[2]*ps[2])
    return pos, rot, scl
print("head world:", world(head))
# children of head
for c in transforms[head].get("m_Children") or []:
    print("  child", tr_name(c["m_PathID"]), "pos", transforms[c["m_PathID"]]["m_LocalPosition"], "world", world(c["m_PathID"]))
# spine.003 world rotation
for nm in ("spine", "spine.001","spine.002","spine.003"):
    for pid in transforms:
        if tr_name(pid)==nm:
            p,r,s = world(pid)
            print(nm, "worldpos", tuple(round(v,4) for v in p), "rot", tuple(round(v,4) for v in r), "scale", tuple(round(v,5) for v in s))
