import UnityPy, collections
env = UnityPy.load("unpack_bundle_3110260")
objs = {o.path_id: o for o in env.objects}
tt = {}
for pid, o in objs.items():
    try: tt[pid] = o.read_typetree()
    except Exception: tt[pid] = None

def n(x): return 0 if x is None else len(x)

print("=== MESHES ===")
for pid,o in objs.items():
    if o.type.name != "Mesh": continue
    m = o.read()
    print(f"{pid:5d} name={m.m_Name!r:30s} verts={n(m.m_Vertices)//3:5d} idx={n(getattr(m,'m_Indices',None))} submeshes={n(m.m_SubMeshes)} uv0={n(getattr(m,'m_UV0',None))} uv1={n(getattr(m,'m_UV1',None))} normals={n(getattr(m,'m_Normals',None))} tangents={n(getattr(m,'m_Tangents',None))} colors={n(getattr(m,'m_Colors',None))} colors32={n(getattr(m,'m_Colors32',None))} compress={getattr(m,'m_CompressedMesh',None) is not None}")
print("=== MATERIALS ===")
for pid,o in objs.items():
    if o.type.name != "Material": continue
    d = tt[pid]
    shader = d.get("m_Shader",{}).get("m_PathID")
    props = d.get("m_SavedProperties",{}).get("m_TexEnvs",[])
    texs = {}
    for p in props:
        if p[1].get("m_Texture",{}).get("m_PathID"): texs[p[0]] = p[1]["m_Texture"]["m_PathID"]
    cols = {}
    for p in d.get("m_SavedProperties",{}).get("m_Colors",[]):
        v = p[1]
        cols[p[0]] = [round(v.get("r",0),3), round(v.get("g",0),3), round(v.get("b",0),3), round(v.get("a",0),3)]
    floats = {}
    for p in d.get("m_SavedProperties",{}).get("m_Floats",[]):
        floats[p[0]] = round(p[1],4)
    print(f"{pid:5d} name={d.get('m_Name')!r:40s} shader={shader} tex={texs} col={cols} floats={floats}")
print("=== TEXTURES ===")
for pid,o in objs.items():
    if o.type.name != "Texture2D": continue
    t = o.read()
    print(f"{pid:5d} name={t.m_Name!r:40s} {t.m_Width}x{t.m_Height} fmt={t.m_TextureFormat} mips={t.m_MipCount} filter={t.m_TextureSettings.m_FilterMode} wrap={t.m_TextureSettings.m_WrapU},{t.m_TextureSettings.m_WrapV} colorspace={t.m_ColorSpace}")
print("=== SHADERS ===")
for pid,o in objs.items():
    if o.type.name != "Shader": continue
    print(pid, (o.read_typetree().get("m_ParsedForm") or {}).get("m_Name"))
print("=== SPRITES ===")
for pid,o in objs.items():
    if o.type.name != "Sprite": continue
    s = o.read_typetree()
    print(pid, s.get("m_Name"), "tex=", (s.get("m_RD") or {}).get("texture",{}).get("m_PathID"))
