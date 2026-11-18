#!/usr/bin/env python3
"""Extract LC's real player rig (Player/ScavengerModel/metarig) from the retail install.

Ground truth for the emote comparison. Writes compare/lc_rig.json:
  bones:  path -> {pos,rot,scl} local TRS (rest as serialized on the prefab)
  order:  parent-before-child path list
  smr:    {name, mesh, bones[path], bindpose[16], localToWorld[16]} in mesh space
  root:   metarig root TRS + ancestry
Also prints a comparison against the copy the tools used before (metarig_rest.json).
"""
import json, math, os, sys
import UnityPy

D = "/mnt/c/Program Files (x86)/Steam/steamapps/common/Lethal Company/Lethal Company_Data"
ASSETS = os.path.join(D, "sharedassets3.assets")
HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, "..", "lc_rig.json"))
TOOL_REST = os.path.normpath(os.path.join(HERE, "..", "..", "metarig_rest.json"))


def gid(tt, key):
    v = tt.get(key)
    return v.get("m_PathID", 0) if isinstance(v, dict) else 0


def mat4(m):
    # Unity serialized Matrix4x4 field order e00..e33
    return [m[f"e{i}{j}"] for i in range(4) for j in range(4)]


def main():
    env = UnityPy.load(ASSETS)
    g, t, smrs = {}, {}, []
    for o in env.objects:
        n = o.type.name
        if n == "GameObject":
            g[o.path_id] = o.read_typetree()
        elif n in ("Transform", "RectTransform"):
            t[o.path_id] = o.read_typetree()
        elif n == "SkinnedMeshRenderer":
            smrs.append(o.read_typetree())

    def name(pid):
        return g.get(gid(t[pid], "m_GameObject"), {}).get("m_Name", "?")

    kids = {}
    for pid, tt in t.items():
        kids.setdefault(gid(tt, "m_Father"), []).append(pid)

    def full(pid):
        chain, c = [], pid
        while c:
            chain.append(name(c))
            f = gid(t[c], "m_Father")
            if f not in t:
                break
            c = f
        return "/".join(reversed(chain))

    # the third-person player skeleton
    root = None
    for pid in t:
        if full(pid) == "Player/ScavengerModel/metarig":
            root = pid
    if root is None:
        sys.exit("player metarig not found in sharedassets3.assets")
    print("metarig root pid", root, "local", t[root]["m_LocalPosition"], t[root]["m_LocalRotation"])

    bones, order = {}, []
    stack = [(root, "")]
    while stack:
        pid, prefix = stack.pop()
        n = name(pid)
        path = n if not prefix else prefix + "/" + n
        tt = t[pid]
        r, p, s = tt["m_LocalRotation"], tt["m_LocalPosition"], tt["m_LocalScale"]
        bones[path] = {"rot": [r["x"], r["y"], r["z"], r["w"]],
                       "pos": [p["x"], p["y"], p["z"]],
                       "scl": [s["x"], s["y"], s["z"]]}
        order.append(path)
        for c in sorted(kids.get(pid, [])):
            stack.append((c, path))
    # parent-before-child order (DFS above is stack-based; sort by depth, stable)
    order.sort(key=lambda q: (q.count("/"), q))

    # skinned meshes bound to this rig
    out_smr = []
    for r in smrs:
        bl = r.get("m_Bones") or []
        paths = []
        for b in bl:
            bid = b["m_PathID"]
            if bid in t:
                paths.append(full(bid))
            else:
                paths.append(None)
        if not any(p and p.startswith("Player/ScavengerModel/metarig") for p in paths):
            continue
        out_smr.append({
            "name": g.get(gid(r, "m_GameObject"), {}).get("m_Name", "?"),
            "rootBonePath": full(gid(r, "m_RootBone")) if gid(r, "m_RootBone") in t else None,
            "bones": paths,
            "bindpose": [mat4(m) for m in (r.get("m_BindPose") or [])],
            "localToWorld": mat4(r["m_LocalToWorldMatrix"]),
        })
        print("SMR", out_smr[-1]["name"], "bones", len(paths), "bindpose", len(out_smr[-1]["bindpose"]))

    doc = {"source": "retail LC install sharedassets3.assets",
           "assets": ASSETS, "root": "metarig",
           "rootAncestry": "Player/ScavengerModel",
           "bones": bones, "order": order, "smr": out_smr}
    json.dump(doc, open(OUT, "w"), indent=1)
    print("wrote", OUT, "bones", len(bones), "smr", len(out_smr))

    # ---- compare with the copy the tooling used
    if os.path.exists(TOOL_REST):
        old = json.load(open(TOOL_REST))
        # old keys are metarig-relative ("" for metarig itself) -> align to "metarig/..."
        old2 = {("metarig" if k == "" else k): v for k, v in old.items()}
        keys = sorted(set(bones) | set(old2), key=lambda q: (q.count("/"), q))
        diffs = []
        for k in keys:
            a, b = bones.get(k), old2.get(k)
            if a is None or b is None:
                diffs.append((k, "missing-real" if a is None else "missing-bundle"))
                continue
            dp = math.dist(a["pos"], b["pos"])
            dq = 2 * math.degrees(math.acos(min(1, abs(sum(x * y for x, y in zip(a["rot"], b["rot"]))))))
            ds = max(abs(a["scl"][i] - b["scl"][i]) for i in range(3))
            if dp > 1e-4 or dq > 0.05 or ds > 1e-4:
                diffs.append((k, dp, dq, ds))
        print(f"\nrig diff vs tool copy ({TOOL_REST}): {len(diffs)} / {len(keys)} entries differ")
        for d in diffs[:80]:
            if len(d) == 2:
                print("  ", d[0], d[1])
            else:
                print(f"   {d[0]:70s} dpos={d[1]*0.561*100:7.2f}cm drot={d[2]:7.2f}deg dscl={d[3]:.4f}")
        big = [d for d in diffs if len(d) > 2 and (d[1] * 0.561 > 0.005 or d[2] > 5)]
        print(f"  material differences (>5mm or >5deg): {len(big)}")
        for d in big[:40]:
            print(f"    {d[0]:70s} dpos={d[1]*0.561*100:7.2f}cm drot={d[2]:7.2f}deg")


if __name__ == "__main__":
    main()
