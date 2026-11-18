"""Read-only helper for inspecting Alterum's built-in sharedassets0.assets.

UnityPy loads the serialized file plus globalgamemanagers.assets (where every
MonoScript lives). MonoBehaviours in a built player carry no type tree, so the
component class is recovered by parsing the fixed MonoBehaviour header by hand:
  PPtr<GameObject> m_GameObject  (int32 fileID, int64 pathID)
  UInt8            m_Enabled
  PPtr<MonoScript> m_Script      (int32 fileID, int64 pathID)
  string           m_Name        (int32 len, bytes)
The bool is padded to a 4-byte boundary, so m_Script starts at offset 16.
"""
import struct
import collections
import UnityPy

BASE = "/mnt/c/AlterumPinned/Alterum_Data/"
MAIN = "sharedassets0.assets"

env = UnityPy.load(BASE + MAIN, BASE + "globalgamemanagers.assets")
FILES = {f.name: f for f in env.files.values()}
PER = {name: {} for name in FILES}
for _o in env.objects:
    PER[_o.assets_file.name][_o.path_id] = _o

MAINFILE = FILES[MAIN]


def resolve(fileid, pathid, cur=MAINFILE):
    if not pathid:
        return None
    if fileid == 0:
        return PER[cur.name].get(pathid)
    ext = cur.externals
    if 1 <= fileid <= len(ext):
        target = ext[fileid - 1].path.split("/")[-1]
        return PER.get(target, {}).get(pathid)
    return None


def name_of_obj(o):
    try:
        return o.read_typetree().get("m_Name")
    except Exception:
        return None


def mb_header(o):
    """Parse m_GameObject / m_Enabled / m_Script / m_Name without a type tree."""
    raw = o.get_raw_data()
    go_fid, go_pid = struct.unpack_from("<iq", raw, 0)
    enabled = raw[12]
    sc_fid, sc_pid = struct.unpack_from("<iq", raw, 16)
    nlen = struct.unpack_from("<i", raw, 28)[0]
    name = raw[32:32 + nlen].decode("utf-8", "replace") if 0 < nlen < 200 else ""
    return dict(go=(go_fid, go_pid), enabled=enabled, script=(sc_fid, sc_pid), name=name)


def script_class(o):
    """ClassName of the MonoBehaviour's script, or None when unreadable."""
    try:
        h = mb_header(o)
    except Exception:
        return None
    so = resolve(h["script"][0], h["script"][1])
    if so is None:
        return None
    try:
        tt = so.read_typetree()
        return tt.get("m_ClassName")
    except Exception:
        return None


def transforms():
    tf = {}
    for pid, o in PER[MAIN].items():
        if o.type.name in ("Transform", "RectTransform"):
            tf[pid] = o.read_typetree()
    return tf


TF = transforms()
CHILDREN = collections.defaultdict(list)
for _pid, _tt in TF.items():
    CHILDREN[_tt["m_Father"]["m_PathID"]].append(_pid)


def tf_name(pid):
    tt = TF.get(pid)
    if not tt:
        return f"<{pid}>"
    o = resolve(0, tt["m_GameObject"]["m_PathID"])
    n = name_of_obj(o) if o else None
    return n if n is not None else "?"


def tf_path(pid):
    parts, seen = [], set()
    while pid and pid in TF and pid not in seen:
        seen.add(pid)
        parts.append(tf_name(pid))
        pid = TF[pid]["m_Father"]["m_PathID"]
    return "/".join(reversed(parts))


def find_tf(name):
    return [pid for pid in TF if tf_name(pid) == name]


_GO_COMP = None


def _build_go_index():
    global _GO_COMP
    _GO_COMP = collections.defaultdict(list)
    for pid, o in PER[MAIN].items():
        if o.type.name in ("Transform", "RectTransform", "GameObject"):
            continue
        if o.type.name == "MonoBehaviour":
            try:
                h = mb_header(o)
            except Exception:
                continue
            _GO_COMP[h["go"][1]].append((o, script_class(o)))
        else:
            try:
                tt = o.read_typetree()
            except Exception:
                continue
            g = tt.get("m_GameObject")
            if isinstance(g, dict):
                gid = g.get("m_PathID", g.get("pathID"))
                if gid:
                    _GO_COMP[gid].append((o, None))


def comps_of_go(go_pid):
    if _GO_COMP is None:
        _build_go_index()
    return _GO_COMP.get(go_pid, [])


def _comps_of_go_slow(go_pid):
    out = []
    for pid, o in PER[MAIN].items():
        if o.type.name in ("Transform", "RectTransform", "GameObject"):
            continue
        if o.type.name == "MonoBehaviour":
            try:
                h = mb_header(o)
            except Exception:
                continue
            if h["go"][1] == go_pid:
                out.append((o, script_class(o)))
        else:
            try:
                tt = o.read_typetree()
            except Exception:
                continue
            g = tt.get("m_GameObject")
            if isinstance(g, dict) and g.get("pathID", g.get("m_PathID")) == go_pid:
                out.append((o, None))
    return out
