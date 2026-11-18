import UnityPy, collections, json
env = UnityPy.load("unpack_bundle_3110260")
objs = {o.path_id: o for o in env.objects}
tt = {pid: (o.read_typetree() if o.type.name!="Mesh" else None) for pid,o in objs.items()}
scripts = {pid: (tt[pid] or {}).get("m_ClassName") for pid,o in objs.items() if o.type.name=="MonoScript"}
transforms = {pid: d for pid,d in tt.items() if objs[pid].type.name=="Transform"}
gos = {pid: d for pid,d in tt.items() if objs[pid].type.name=="GameObject"}
go_comps = collections.defaultdict(list)
for pid,d in tt.items():
    if not d: continue
    go = d.get("m_GameObject")
    if isinstance(go, dict) and go.get("m_PathID"): go_comps[go["m_PathID"]].append(pid)

# material info
mats = {}
for pid,d in tt.items():
    if objs[pid].type.name != "Material": continue
    sp = d.get("m_SavedProperties",{})
    texs = {p[0]: p[1].get("m_Texture",{}).get("m_PathID") for p in sp.get("m_TexEnvs",[]) if p[1].get("m_Texture",{}).get("m_PathID")}
    cols = {}
    for p in sp.get("m_Colors",[]):
        v = p[1]; cols[p[0]] = tuple(round(v.get(k,0),4) for k in ("r","g","b","a"))
    fl = {p[0]: round(p[1],4) for p in sp.get("m_Floats",[])}
    mats[pid] = dict(name=d.get("m_Name"), shader=d.get("m_Shader",{}).get("m_PathID"), tex=texs,
                     base=cols.get("_BaseColor"), metal=fl.get("_Metallic"), smooth=fl.get("_Smoothness"),
                     surf=fl.get("_SurfaceType"), blend=fl.get("_BlendMode"), double=fl.get("_DoubleSidedEnable"),
                     zwrite=fl.get("_ZWrite"))

def walk(tpid, out):
    d = transforms[tpid]
    gp = d["m_GameObject"]["m_PathID"]
    comps = []
    for c in go_comps[gp]:
        t = objs[c].type.name
        if t == "MeshRenderer":
            comps.append(("MR", [m["m_PathID"] for m in (tt[c].get("m_Materials") or [])]))
        elif t == "MeshFilter":
            comps.append(("MF", tt[c]["m_Mesh"]["m_PathID"]))
        elif t == "MonoBehaviour":
            s = (tt[c] or {}).get("m_Script") or {}
            comps.append(("MB", scripts.get(s.get("m_PathID"))))
        elif t == "Animator":
            comps.append(("Animator", tt[c].get("m_Controller",{}).get("m_PathID")))
    out.append(dict(pid=tpid, name=gos[gp]["m_Name"], active=gos[gp].get("m_IsActive"),
                    pos=d["m_LocalPosition"], rot=d["m_LocalRotation"], scale=d["m_LocalScale"], comps=comps))
    for c in d.get("m_Children") or []: walk(c["m_PathID"], out)

used_meshes=set(); used_mats=set()
cosmetics = {}
for pid,d in tt.items():
    if objs[pid].type.name != "MonoBehaviour": continue
    s = (d or {}).get("m_Script") or {}
    if scripts.get(s.get("m_PathID")) != "CosmeticInstance": continue
    gop = d["m_GameObject"]["m_PathID"]
    tpid = next(tp for tp,tdd in transforms.items() if tdd["m_GameObject"]["m_PathID"]==gop)
    out=[]; walk(tpid,out)
    cosmetics[d["cosmeticId"]] = dict(type=d.get("cosmeticType"), icon=d.get("icon",{}).get("m_PathID"), nodes=out)
    for nd in out:
        for kind,val in nd["comps"]:
            if kind=="MF": used_meshes.add(val)
            if kind=="MR": used_mats.update(val)
print("cosmetics:", len(cosmetics), "meshes:", len(used_meshes), "materials:", len(used_mats))
used_tex = set()
for m in used_mats:
    for k,v in mats[m]["tex"].items():
        used_tex.add(v)
print("used textures:", len(used_tex))
for t in sorted(used_tex):
    o = objs[t]; tt2 = tt[t]
    print(f"  tex {t} {tt2.get('m_Name')!r} {tt2.get('m_Width')}x{tt2.get('m_Height')} fmt={tt2.get('m_TextureFormat')} mips={tt2.get('m_MipCount')}")
print()
for m in sorted(used_mats):
    i = mats[m]
    print(f"  mat {m} {i['name']!r:36s} base={i['base']} metal={i['metal']} smooth={i['smooth']} surf={i['surf']} blend={i['blend']} double={i['double']} tex={i['tex']}")
print()
for cid in sorted(cosmetics):
    c = cosmetics[cid]
    ms = sorted({v for nd in c['nodes'] for k,v in nd['comps'] if k=='MF'})
    mm = sorted({v for nd in c['nodes'] for k,v in nd['comps'] if k=='MR' for v in (v if isinstance(v,list) else [v])})
    print(f"  {cid:26s} type={c['type']} meshes={ms} mats={mm}")
