import UnityPy, collections
env = UnityPy.load("unpack_bundle_3110260")
objs = {o.path_id: o for o in env.objects}
tt = {pid: (o.read_typetree() if o.type.name!="Mesh" else None) for pid,o in objs.items()}
def n(x): return 0 if x is None else len(x)
print("=== MESHES ===")
for pid,o in objs.items():
    if o.type.name != "Mesh": continue
    m = o.read()
    cm = getattr(m,'m_CompressedMesh',None)
    print(f"{pid:5d} {m.m_Name!r:30s} verts={n(m.m_Vertices)//3:5d} idx={n(getattr(m,'m_Indices',None))} sub={n(m.m_SubMeshes)} uv0={n(getattr(m,'m_UV0',None))} norm={n(getattr(m,'m_Normals',None))} tan={n(getattr(m,'m_Tangents',None))} col={n(getattr(m,'m_Colors',None))} cpress={(cm.m_Vertices.m_NumItems if cm and cm.m_Vertices else 0)}")
print("=== MATERIALS (id name shader basemap normal col metallic smooth) ===")
for pid,o in objs.items():
    if o.type.name != "Material": continue
    d = tt[pid]
    sp = d.get("m_SavedProperties",{})
    texs = {p[0]: p[1].get("m_Texture",{}).get("m_PathID") for p in sp.get("m_TexEnvs",[]) if p[1].get("m_Texture",{}).get("m_PathID")}
    cols = {p[0]: tuple(round(v.get(k,0),3) for k in ("r","g","b","a")) for p in sp.get("m_Colors",[])}
    fl = {p[0]: round(p[1],3) for p in sp.get("m_Floats",[])}
    print(f"{pid:5d} {d.get('m_Name')!r:40s} shader={d.get('m_Shader',{}).get('m_PathID')} tex={texs} base={cols.get('_BaseColor')} metal={fl.get('_Metallic')} smooth={fl.get('_Smoothness')} surf={fl.get('_SurfaceType')} blend={fl.get('_BlendMode')} double={fl.get('_DoubleSidedEnable')}")
