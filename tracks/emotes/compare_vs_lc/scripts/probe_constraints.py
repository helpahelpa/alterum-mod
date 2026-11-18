#!/usr/bin/env python3
"""Decode the Animation Rigging components on LC's player metarig prefab (retail install)."""
import os, sys
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "compare", "scripts"))
import UnityPy
D = "/mnt/c/Program Files (x86)/Steam/steamapps/common/Lethal Company/Lethal Company_Data"
S3 = os.path.join(D, "sharedassets3.assets")
env = UnityPy.load(S3)
g, t, mbs = {}, {}, {}
for o in env.objects:
    n = o.type.name
    if n == "GameObject": g[o.path_id] = o.read_typetree()
    elif n in ("Transform", "RectTransform"): t[o.path_id] = o.read_typetree()
    elif n == "MonoBehaviour": mbs[o.path_id] = o
def gid(tt, k):
    v = tt.get(k); return v.get("m_PathID", 0) if isinstance(v, dict) else 0
def nm(pid): return g.get(gid(t[pid], "m_GameObject"), {}).get("m_Name", "?")
def full(pid):
    ch = []; c = pid
    while c:
        ch.append(nm(c)); f = gid(t[c], "m_Father")
        if f not in t: break
        c = f
    return "/".join(reversed(ch))
tpath = {p: full(p) for p in t}
go2t = {}
for tp in t: go2t[gid(t[tp], "m_GameObject")] = tp
AF = next(iter(env.objects)).assets_file
script_cache = {}
def resolve_script(fid, pid):
    key = (fid, pid)
    if key in script_cache: return script_cache[key]
    if fid == 0: e = env
    else:
        ex = AF.externals
        if fid - 1 >= len(ex): return None
        fp = ex[fid - 1].path
        if not os.path.isabs(fp): fp = os.path.join(D, fp)
        if not os.path.exists(fp): return None
        e = UnityPy.load(fp)
    r = None
    for o in e.objects:
        if o.path_id == pid and o.type.name == "MonoScript":
            tt = o.read_typetree(); r = (tt.get("m_ClassName"), tt.get("m_Namespace")); break
    script_cache[key] = r
    return r
rows = []
for pid, tt in g.items():
    tp = go2t.get(pid)
    if tp is None: continue
    p = tpath[tp]
    if not p.startswith("Player/ScavengerModel/metarig"): continue
    for c in tt.get("m_Component") or []:
        cp = c.get("component") or {}
        cpid = cp.get("m_PathID"); cfid = cp.get("m_FileID")
        if cfid == 0 and cpid in mbs:
            o = mbs[cpid]; h = o.parse_monobehaviour_head()
            sc = resolve_script(h.m_Script.m_FileID, h.m_Script.m_PathID)
            rows.append((p, cpid, o, sc))
for p, cpid, o, sc in sorted(rows, key=lambda r: r[0]):
    name = (sc[0] if sc else "?")
    if "IK" not in name and "Rotation" not in name and "Rig" not in name: continue
    tt = o.read_typetree()
    keys = [k for k in tt.keys() if k.lower().endswith("weight") or k.startswith("m_Weight")]
    def fld(k): return tt.get(k)
    def ref(k):
        v = tt.get(k); return v.get("m_PathID", v) if isinstance(v, dict) else v
    extra = {}
    for k, v in tt.items():
        if k in ("m_GameObject", "m_Script", "m_Weight"): continue
        if isinstance(v, dict) and "m_PathID" in v and k not in ("m_Script",):
            extra[k] = v["m_PathID"]
        elif isinstance(v, (int, float, bool, str)):
            extra[k] = v
    print(f"{p:70s} {name:28s} {extra}")
