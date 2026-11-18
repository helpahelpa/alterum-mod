import UnityPy, sys, zlib, collections

path = "../../tracks/cosmetics/unpack_bundle_3110260"
env = UnityPy.load(path)

gobs, trs = {}, {}
for obj in env.objects:
    if obj.type.name == "GameObject":
        tt = obj.read_typetree()
        gobs[obj.path_id] = tt
    elif obj.type.name == "Transform":
        trs[obj.path_id] = obj.read_typetree()

def father(tt):
    # m_Father: {m_FileID, m_PathID}
    return tt.get("m_Father", {}).get("m_PathID", 0)

children = collections.defaultdict(list)
for pid, tt in trs.items():
    children[father(tt)].append(pid)

def name_of(tr_pid):
    go = trs[tr_pid].get("m_GameObject", {}).get("m_PathID", 0)
    return gobs.get(go, {}).get("m_Name", "?")

# roots = transforms whose father is 0 or whose father is not in trs
roots = [pid for pid in trs if father(pid) == 0 or father(pid) not in trs]
print("transforms:", len(trs), "roots:", len(roots))
for r in roots:
    print("root:", name_of(r), r)
print()

def walk(pid, prefix, depth, out):
    nm = name_of(pid)
    p = nm if prefix == "" else prefix + "/" + nm
    tt = trs[pid]
    out.append((p, tt))
    for ch in children.get(pid, []):
        walk(ch, p, depth+1, out)

for r in roots:
    nm = name_of(r)
    if "Scavenger" not in nm and "Astronaut" not in nm: continue
    out = []
    walk(r, "", 0, out)
    print("="*70); print("MODEL:", nm, "nodes:", len(out))
    for p, tt in out:
        crc = zlib.crc32(p.encode()) & 0xffffffff
        pos = tt.get("m_LocalPosition", {}); rot = tt.get("m_LocalRotation", {}); scl = tt.get("m_LocalScale", {})
        print(f"  {p:70s} crc={crc:10d} pos=({pos.get('x',0):.4f},{pos.get('y',0):.4f},{pos.get('z',0):.4f}) rot=({rot.get('x',0):.4f},{rot.get('y',0):.4f},{rot.get('z',0):.4f},{rot.get('w',0):.4f}) scl=({scl.get('x',0):.3f},{scl.get('y',0):.3f},{scl.get('z',0):.3f})")
