import UnityPy, json, struct, math
from UnityPy.helpers.MeshHelper import MeshHandler
env = UnityPy.load("unpack_bundle_3110260")
objs = {o.path_id:o for o in env.objects}
trees = {}
for pid,o in objs.items():
    try: trees[pid]=o.read_typetree()
    except Exception: trees[pid]=None
scripts = {pid:(trees[pid] or {}).get("m_ClassName") for pid,o in objs.items() if o.type.name=="MonoScript"}
transforms = {pid:d for pid,d in trees.items() if objs[pid].type.name=="Transform"}
gos = {pid:d for pid,d in trees.items() if objs[pid].type.name=="GameObject"}
go_comps={}
for pid,d in trees.items():
    if not d: continue
    go=d.get("m_GameObject")
    if isinstance(go,dict) and go.get("m_PathID"): go_comps.setdefault(go["m_PathID"],[]).append(pid)
mesh_bounds={}
for pid,o in objs.items():
    if o.type.name!="Mesh": continue
    m=o.read(); h=MeshHandler(m); h.process()
    vs=h.m_Vertices
    mesh_bounds[pid]=([min(v[k] for v in vs) for k in range(3)],[max(v[k] for v in vs) for k in range(3)])

def qmul(a,b):
    ax,ay,az,aw=a; bx,by,bz,bw=b
    return (aw*bx+ax*bw+ay*bz-az*by, aw*by-ax*bz+ay*bw+az*bx, aw*bz+ax*by-ay*bx+az*bw, aw*bw-ax*bx-ay*by-az*bz)
def qrot(q,v):
    x,y,z,w=q; vx,vy,vz=v
    tx=2*(y*vz-z*vy); ty=2*(z*vx-x*vz); tz=2*(x*vy-y*vx)
    return (vx+w*tx+(y*tz-z*ty), vy+w*ty+(z*tx-x*tz), vz+w*tz+(x*ty-y*tx))

# collect cosmetics with their placement-node local TRS and AABB in root-local space
def node_matrix(n, parent_rot=(0,0,0,1), parent_pos=(0,0,0), parent_scl=(1,1,1)):
    """compose: world = parent * local; return (rot, pos, scl)"""
    lp=n["pos"]; lr=n["rot"]; ls=n["scale"]
    sp=(lp[0]*parent_scl[0], lp[1]*parent_scl[1], lp[2]*parent_scl[2])
    rv=qrot(parent_rot, sp)
    pos=(parent_pos[0]+rv[0], parent_pos[1]+rv[1], parent_pos[2]+rv[2])
    rot=qmul(parent_rot, tuple(lr))
    scl=(parent_scl[0]*ls[0], parent_scl[1]*ls[1], parent_scl[2]*ls[2])
    return rot,pos,scl

def collect(n, rot=(0,0,0,1), pos=(0,0,0), scl=(1,1,1), out=None):
    if out is None: out=[]
    r,p,s = node_matrix(n, rot, pos, scl)
    if n["mesh"] is not None and n["mesh"] in mesh_bounds:
        mn,mx = mesh_bounds[n["mesh"]]
        for cx in (mn[0],mx[0]):
            for cy in (mn[1],mx[1]):
                for cz in (mn[2],mx[2]):
                    v=(cx*s[0], cy*s[1], cz*s[2])
                    rv=qrot(r,v)
                    out.append((p[0]+rv[0],p[1]+rv[1],p[2]+rv[2]))
    for c in n["children"]:
        collect(c, r, p, s, out)
    return out

cos={}
for pid,d in trees.items():
    if objs[pid].type.name!="MonoBehaviour": continue
    s=(d or {}).get("m_Script") or {}
    if scripts.get(s.get("m_PathID"))!="CosmeticInstance": continue
    gop=d["m_GameObject"]["m_PathID"]
    tpid=next(tp for tp,tdd in transforms.items() if tdd["m_GameObject"]["m_PathID"]==gop)
    def build(tp):
        d2=transforms[tp]; gp=d2["m_GameObject"]["m_PathID"]
        node=dict(name=gos[gp]["m_Name"], pos=[d2["m_LocalPosition"][k] for k in "xyz"],
                  rot=[d2["m_LocalRotation"][k] for k in "xyzw"],
                  scale=[d2["m_LocalScale"][k] for k in "xyz"], mesh=None, materials=[], children=[])
        for c in go_comps.get(gp,[]):
            if objs[c].type.name=="MeshFilter": node["mesh"]=trees[c]["m_Mesh"]["m_PathID"]
            if objs[c].type.name=="MeshRenderer": node["materials"]=[m["m_PathID"] for m in trees[c].get("m_Materials") or []]
        node["children"]=[build(ch["m_PathID"]) for ch in d2.get("m_Children") or []]
        return node
    root=build(tpid)
    pts=collect(root["children"][0]) if root["children"] else []
    if not pts: continue
    mn=[min(p[k] for p in pts) for k in range(3)]; mx=[max(p[k] for p in pts) for k in range(3)]
    cos[d["cosmeticId"]]=(int(d.get("cosmeticType")), mn, mx, root["children"][0]["name"],
                          root["children"][0]["pos"], root["children"][0]["scale"])

W_src, top_src = 0.40216, 0.370123
print(f"source head: width={W_src:.4f} top={top_src:.4f}  (LC head-bone local units)")
print(f"{'id':24s} {'type':4s} {'node':16s} {'yoff':>8s} {'minY':>8s} {'maxY':>8s} {'width':>8s} {'u=minY/top':>10s} {'w/Wsrc':>7s}")
for cid in sorted(cos):
    t,mn,mx,node,pos,scl = cos[cid]
    w = mx[0]-mn[0]
    print(f"{cid:24s} {t:4d} {node[:16]:16s} {pos[1]:8.4f} {mn[1]:8.4f} {mx[1]:8.4f} {w:8.4f} {mn[1]/top_src:10.3f} {w/W_src:7.3f}")
