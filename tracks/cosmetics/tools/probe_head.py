import UnityPy, collections
env = UnityPy.load("unpack_bundle_70132")
objs = {o.path_id: o for o in env.objects}
cnt = collections.Counter(o.type.name for o in env.objects)
print(dict(cnt))
tt = {}
for pid,o in objs.items():
    try: tt[pid] = o.read_typetree()
    except Exception: tt[pid] = None
# find prefab with 'ScavengerModel'
for pid,d in tt.items():
    if objs[pid].type.name == "GameObject" and d and d.get("m_Name") == "ScavengerModel":
        print("ScavengerModel go", pid, [ (c['m_Component']['component']['m_PathID'] if 'm_Component' in c else c) for c in d.get("m_Component",[])])
# list skinned mesh renderers
for pid,d in tt.items():
    if objs[pid].type.name != "SkinnedMeshRenderer": continue
    go = d.get("m_GameObject",{}).get("m_PathID")
    goname = tt[go].get("m_Name") if go in tt else "?"
    print("SMR", pid, "go", goname, "mesh", d.get("m_Mesh",{}).get("m_PathID"), "bones", len(d.get("m_Bones") or []), "rootBone", d.get("m_RootBone",{}).get("m_PathID"))
