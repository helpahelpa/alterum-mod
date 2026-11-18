import UnityPy, collections, sys
p = "unpack_bundle_3110260"
env = UnityPy.load(p)
cnt = collections.Counter()
for o in env.objects:
    cnt[o.type.name] += 1
print("objects:", dict(cnt))
# find MonoBehaviour typetree names
mb = [o for o in env.objects if o.type.name == "MonoBehaviour"]
names = collections.Counter()
for o in mb:
    try:
        d = o.read_typetree()
        ms = d.get("m_Script")
        names[(d.get("m_Name","?"))] += 1
    except Exception as e:
        names["ERR "+str(e)[:60]] += 1
print("monobehaviour names:", dict(names))
