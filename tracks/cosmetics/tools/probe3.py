import UnityPy, collections, json
env = UnityPy.load("unpack_bundle_3110260")
objs = {o.path_id: o for o in env.objects}
tt = {}
for pid, o in objs.items():
    try: tt[pid] = o.read_typetree()
    except Exception: tt[pid] = None

scripts = {pid: (tt[pid] or {}).get("m_ClassName") for pid,o in objs.items() if o.type.name=="MonoScript"}

# map transform -> gameobject and children
transforms = {pid: d for pid,d in tt.items() if objs[pid].type.name=="Transform"}
gos = {pid: d for pid,d in tt.items() if objs[pid].type.name=="GameObject"}

# component lookup: go pid -> list of component pids
go_comps = collections.defaultdict(list)
for pid,d in tt.items():
    if not d: continue
    go = d.get("m_GameObject")
    if isinstance(go, dict) and go.get("m_PathID"): go_comps[go["m_PathID"]].append(pid)

def go_of_tr(tpid): return transforms[tpid]["m_GameObject"]["m_PathID"]

def dump(tpid, depth, out):
    d = transforms[tpid]
    gp = d["m_GameObject"]["m_PathID"]
    name = gos[gp]["m_Name"]
    lp = d["m_LocalPosition"]; lr = d["m_LocalRotation"]; ls = d["m_LocalScale"]
    comps = []
    for c in go_comps[gp]:
        t = objs[c].type.name
        if t == "MonoBehaviour":
            dd = tt[c] or {}
            s = dd.get("m_Script") or {}
            comps.append("MB:"+str(scripts.get(s.get("m_PathID"))))
        elif t == "MeshFilter":
            mesh = tt[c]["m_Mesh"]["m_PathID"]
            comps.append("MF:%d" % mesh)
        elif t == "MeshRenderer":
            mats = [m["m_PathID"] for m in (tt[c].get("m_Materials") or [])]
            comps.append("MR:%s" % mats)
        else:
            comps.append(t)
    out.append({"depth":depth,"path_id":tpid,"name":name,
        "pos":[round(lp["x"],5),round(lp["y"],5),round(lp["z"],5)],
        "rot":[round(lr["x"],5),round(lr["y"],5),round(lr["z"],5),round(lr["w"],5)],
        "scale":[round(ls["x"],5),round(ls["y"],5),round(ls["z"],5)],
        "comps":comps, "active": gos[gp].get("m_IsActive")})
    for c in d.get("m_Children") or []:
        dump(c["m_PathID"], depth+1, out)

# roots = transforms with m_Father == 0
roots = [pid for pid,d in transforms.items() if (d.get("m_Father") or {}).get("m_PathID",0)==0]
print("root transforms:", len(roots))
res = {}
for pid,d in tt.items():
    if objs[pid].type.name != "MonoBehaviour": continue
    s = (d or {}).get("m_Script") or {}
    if scripts.get(s.get("m_PathID")) != "CosmeticInstance": continue
    gop = d["m_GameObject"]["m_PathID"]
    # find root transform
    tpid = None
    for tp, tdd in transforms.items():
        if tdd["m_GameObject"]["m_PathID"] == gop: tpid = tp; break
    out = []
    dump(tpid, 0, out)
    res[d["cosmeticId"]] = out
for k in sorted(res):
    print("==", k)
    for n in res[k]:
        print("  "*n["depth"] + f"{n['name']} pos={n['pos']} rot={n['rot']} scale={n['scale']} {n['comps']} active={n['active']}")
