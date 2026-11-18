import UnityPy, math
from UnityPy.helpers.MeshHelper import MeshHandler
env = UnityPy.load("unpack_bundle_70132")
objs = {o.path_id: o for o in env.objects}
tt = {}
for pid,o in objs.items():
    try: tt[pid] = o.read_typetree()
    except Exception: tt[pid] = None
transforms = {pid:d for pid,d in tt.items() if objs[pid].type.name in ("Transform","RectTransform")}
gos = {pid:d for pid,d in tt.items() if objs[pid].type.name=="GameObject"}
def nm(pid): return gos[transforms[pid]["m_GameObject"]["m_PathID"]]["m_Name"]
def chain(pid):
    out=[]
    while pid:
        out.append(pid)
        pid = (transforms[pid].get("m_Father") or {}).get("m_PathID",0)
    return out
def qmul(a,b):
    ax,ay,az,aw=a; bx,by,bz,bw=b
    return (aw*bx+ax*bw+ay*bz-az*by, aw*by-ax*bz+ay*bw+az*bx, aw*bz+ax*by-ay*bx+az*bw, aw*bw-ax*bx-ay*by-az*bz)
def qinv(q): return (-q[0],-q[1],-q[2],q[3])
def qrot(q,v):
    x,y,z,w=q; vx,vy,vz=v
    tx = 2*(y*vz - z*vy); ty = 2*(z*vx - x*vz); tz = 2*(x*vy - y*vx)
    return (vx + w*tx + (y*tz - z*ty), vy + w*ty + (z*tx - x*tz), vz + w*tz + (x*ty - y*tx))
metarig = next(p for p in transforms if nm(p)=="metarig")
head = next(p for p in transforms if nm(p)=="spine.004")
def rel_rot(pid):
    # rotation of pid relative to metarig, and position relative to metarig (unit scales)
    q=(0,0,0,1); pos=(0,0,0)
    c = chain(pid)
    c = c[:c.index(metarig)]  # from pid up to (not incl) metarig
    for p in c:
        d=transforms[p]; lr=d["m_LocalRotation"]; lp=d["m_LocalPosition"]
        pr=(lr["x"],lr["y"],lr["z"],lr["w"]); pv=(lp["x"],lp["y"],lp["z"])
        rv=qrot(q,pv)
        pos=(pos[0]+rv[0],pos[1]+rv[1],pos[2]+rv[2]); q=qmul(q,pr)
    return pos,q
for name in ("spine","spine.001","spine.002","spine.003","spine.004","arm.R_upper","arm.R_lower"):
    pid = next((p for p in transforms if nm(p)==name), None)
    if not pid: print(name,"missing"); continue
    pos,q = rel_rot(pid)
    print(f"{name:14s} metarig-local pos={tuple(round(v,4) for v in pos)} rot={tuple(round(v,4) for v in q)}")
# head-weighted vertices via bindpose
smr = next(d for pid,d in tt.items() if objs[pid].type.name=="SkinnedMeshRenderer" and (tt.get(d["m_GameObject"]["m_PathID"],{}) or {}).get("m_Name")=="LOD1")
bones = [b["m_PathID"] for b in smr["m_Bones"]]
print("bones", len(bones), "rootBone", smr["m_RootBone"]["m_PathID"])
bi = [i for i,b in enumerate(bones) if nm(b)=="spine.004"]
print("head bone index", bi, [nm(bones[i]) for i in bi])
mesh_obj = objs[smr["m_Mesh"]["m_PathID"]]
mesh = mesh_obj.read()
h = MeshHandler(mesh); h.process()
print("mesh verts", len(h.m_Vertices), "skin", len(mesh.m_Skin or []), "bindposes", len(mesh.m_BindPose or []))
import itertools
idx = bi[0]
bp = mesh.m_BindPose[idx]
# Matrix4x4 column-major
m = [[bp[k*4+r] for k in range(4)] for r in range(4)]
def apply(m,v):
    x,y,z = v
    return (m[0][0]*x+m[0][1]*y+m[0][2]*z+m[0][3],
            m[1][0]*x+m[1][1]*y+m[1][2]*z+m[1][3],
            m[2][0]*x+m[2][1]*y+m[2][2]*z+m[2][3])
mins=[1e9]*3; maxs=[-1e9]*3; cnt=0
for vi,v in enumerate(h.m_Vertices):
    s = mesh.m_Skin[vi]
    w = [s.weight_0_, s.weight_1_, s.weight_2_, s.weight_3_]
    idxs = [s.boneIndex_0_, s.boneIndex_1_, s.boneIndex_2_, s.boneIndex_3_]
    wsum = sum(w[i] for i in range(4) if idxs[i]==idx)
    if wsum < 0.5: continue
    p = apply(m,v)
    cnt+=1
    for k in range(3):
        mins[k]=min(mins[k],p[k]); maxs[k]=max(maxs[k],p[k])
print("head-weighted verts", cnt)
print("head-local bounds min", [round(v,4) for v in mins], "max", [round(v,4) for v in maxs])
print("size", [round(maxs[k]-mins[k],4) for k in range(3)])
