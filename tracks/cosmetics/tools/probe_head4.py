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
        out.append(pid); pid = (transforms[pid].get("m_Father") or {}).get("m_PathID",0)
    return out
def qmul(a,b):
    ax,ay,az,aw=a; bx,by,bz,bw=b
    return (aw*bx+ax*bw+ay*bz-az*by, aw*by-ax*bz+ay*bw+az*bx, aw*bz+ax*by-ay*bx+az*bw, aw*bw-ax*bx-ay*by-az*bz)
def qrot(q,v):
    x,y,z,w=q; vx,vy,vz=v
    tx = 2*(y*vz - z*vy); ty = 2*(z*vx - x*vz); tz = 2*(x*vy - y*vx)
    return (vx + w*tx + (y*tz - z*ty), vy + w*ty + (z*tx - x*tz), vz + w*tz + (x*ty - y*tx))
metarig = next(p for p in transforms if nm(p)=="metarig")
def rel(pid):
    c = chain(pid); c = list(reversed(c[:c.index(metarig)]))   # metarig -> ... -> pid
    q=(0,0,0,1); pos=(0,0,0); s=1.0
    for p in c:
        d=transforms[p]; lr=d["m_LocalRotation"]; lp=d["m_LocalPosition"]; ls=d["m_LocalScale"]
        pr=(lr["x"],lr["y"],lr["z"],lr["w"]); pv=(lp["x"]*ls["x"],lp["y"]*ls["y"],lp["z"]*ls["z"])
        rv=qrot(q,pv); pos=(pos[0]+rv[0],pos[1]+rv[1],pos[2]+rv[2])
        q=qmul(q,pr)
    return pos,q
for name in ("spine","spine.001","spine.002","spine.003","spine.004","spine.004_end"):
    pid = next((p for p in transforms if nm(p)==name), None)
    if not pid: print(name,"missing"); continue
    pos,q = rel(pid)
    up = qrot(q,(0,1,0)); fwd = qrot(q,(0,0,1))
    print(f"{name:14s} pos={tuple(round(v,4) for v in pos)} rot={tuple(round(v,4) for v in q)} localY->metarig={tuple(round(v,3) for v in up)} localZ->{tuple(round(v,3) for v in fwd)}")
# mesh 33 skin + bindpose
mesh_obj = objs[33]; mesh = mesh_obj.read()
h = MeshHandler(mesh); h.process()
print("mesh", repr(mesh.m_Name), "verts", len(h.m_Vertices), "bindpose", len(mesh.m_BindPose), "boneIdx", len(h.m_BoneIndices or []), "boneW", len(h.m_BoneWeights or []))
smr = next(d for pid,d in tt.items() if objs[pid].type.name=="SkinnedMeshRenderer" and (tt.get(d["m_GameObject"]["m_PathID"],{}) or {}).get("m_Name")=="LOD1")
bones=[b["m_PathID"] for b in smr["m_Bones"]]
idx = bones.index(next(b for b in bones if nm(b)=="spine.004"))
bp = mesh.m_BindPose[idx]
M = [[bp.e00,bp.e01,bp.e02,bp.e03],[bp.e10,bp.e11,bp.e12,bp.e13],[bp.e20,bp.e21,bp.e22,bp.e23],[0,0,0,1]]
def apply(v):
    x,y,z=v
    return (M[0][0]*x+M[0][1]*y+M[0][2]*z+M[0][3], M[1][0]*x+M[1][1]*y+M[1][2]*z+M[1][3], M[2][0]*x+M[2][1]*y+M[2][2]*z+M[2][3])
mins=[1e9]*3; maxs=[-1e9]*3; cnt=0; ws=0.0
for vi,v in enumerate(h.m_Vertices):
    bi = h.m_BoneIndices[vi]; bw = h.m_BoneWeights[vi]
    w = sum(bw[k] for k in range(4) if bi[k]==idx)
    if w < 0.5: continue
    p = apply(v); cnt+=1
    for k in range(3): mins[k]=min(mins[k],p[k]); maxs[k]=max(maxs[k],p[k])
print("head-weighted verts", cnt)
print("head-local bounds min", [round(v,4) for v in mins], "max", [round(v,4) for v in maxs], "size", [round(maxs[k]-mins[k],4) for k in range(3)])
print("bindpose translation (head origin in mesh space):", M[0][3], M[1][3], M[2][3])
