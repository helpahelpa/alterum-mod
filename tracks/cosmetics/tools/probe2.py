import UnityPy, collections
env = UnityPy.load("unpack_bundle_3110260")
objs = list(env.objects)
by_path = {}
for o in objs: by_path[o.path_id] = o

# MonoScript names
scripts = {}
for o in objs:
    if o.type.name == "MonoScript":
        d = o.read_typetree()
        scripts[o.path_id] = d.get("m_ClassName")
print("scripts:", scripts)

def cls_of_mb(o):
    d = o.read_typetree()
    s = d.get("m_Script")
    if s is None: return None, d
    pid = s["m_PathID"]
    return scripts.get(pid, "ext:%d" % pid), d

# GameObjects: name + components
gos = {}
for o in objs:
    if o.type.name != "GameObject": continue
    d = o.read_typetree()
    gos[o.path_id] = d
print("gameobjects:", len(gos))
# transforms
tr = {}
for o in objs:
    if o.type.name != "Transform": continue
    tr[o.path_id] = o.read_typetree()
print("transforms:", len(tr))

# MonoBehaviour with CosmeticInstance
cos = []
for o in objs:
    if o.type.name != "MonoBehaviour": continue
    cname, d = cls_of_mb(o)
    if cname == "CosmeticInstance":
        cos.append((o.path_id, d))
print("CosmeticInstance count:", len(cos))
for pid, d in cos[:30]:
    go = d["m_GameObject"]["m_PathID"]
    g = gos[go]
    print(pid, "go=", g["m_Name"], "id=", d.get("cosmeticId"), "type=", d.get("cosmeticType"), "icon=", d.get("icon",{}).get("m_PathID"))
