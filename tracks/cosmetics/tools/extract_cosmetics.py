#!/usr/bin/env python3
"""Extract MoreCompany 1.14.0 built-in cosmetics into an engine-neutral pack.

Input : the carved embedded UnityFS bundles from MoreCompany.dll
        (tracks/cosmetics/unpack_bundle_3110260 = morecompany.cosmetics,
         tracks/cosmetics/unpack_bundle_70132    = morecompany.assets)
Output: mod/AlterumModelSwap.Hot/cosmetics.apack  (embedded resource of the hot assembly)
        tools/out/audit.json                      (human/machine readable extraction report)

No Unity serialized asset crosses into the game: only numeric arrays, PNG-encoded
RGBA8 pixels, floats and ids. The runtime rebuilds Mesh/Texture2D/Material with
UnityEngine APIs against URP.

Pack layout (little endian):
    magic  8 bytes  b"ACPK1\0\0\0"
    u32    manifest byte length
    bytes  manifest JSON (UTF-8)
    bytes  blob area (all buffers concatenated, 4-byte aligned)
"""
import hashlib
import io
import json
import os
import struct
import sys

import UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler

HERE = os.path.dirname(os.path.abspath(__file__))
TRACK = os.path.dirname(HERE)
COSMETICS_BUNDLE = os.path.join(TRACK, "unpack_bundle_3110260")
ASSETS_BUNDLE = os.path.join(TRACK, "unpack_bundle_70132")
OUT_PACK = os.path.join(TRACK, "mod", "AlterumModelSwap.Hot", "cosmetics.apack")
OUT_AUDIT = os.path.join(HERE, "out", "audit.json")

TEX_MAX = 0  # 0 = keep source resolution


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def qinv(q):
    return (-q[0], -q[1], -q[2], q[3])


def qrot(q, v):
    x, y, z, w = q
    vx, vy, vz = v
    tx = 2 * (y * vz - z * vy)
    ty = 2 * (z * vx - x * vz)
    tz = 2 * (x * vy - y * vx)
    return (vx + w * tx + (y * tz - z * ty),
            vy + w * ty + (z * tx - x * tz),
            vz + w * tz + (x * ty - y * tx))


def load_bundle(path):
    env = UnityPy.load(path)
    objs = {o.path_id: o for o in env.objects}
    trees = {}
    for pid, o in objs.items():
        try:
            trees[pid] = o.read_typetree()
        except Exception:
            trees[pid] = None
    return env, objs, trees


class Blob:
    def __init__(self):
        self.buf = bytearray()
        self.entries = []

    def add(self, data, align=4):
        while len(self.buf) % align:
            self.buf.append(0)
        off = len(self.buf)
        self.buf.extend(data)
        return {"off": off, "len": len(data)}


def aabb_in_root(nodes, mesh_bounds):
    """Union AABB of the cosmetic's meshes in the prefab root's local space."""
    out = []

    def compose(n, rot, pos, scl):
        lp, lr, ls = n["pos"], n["rot"], n["scale"]
        sp = (lp[0] * scl[0], lp[1] * scl[1], lp[2] * scl[2])
        rv = qrot(rot, sp)
        p = (pos[0] + rv[0], pos[1] + rv[1], pos[2] + rv[2])
        r = qmul(rot, tuple(lr))
        s = (scl[0] * ls[0], scl[1] * ls[1], scl[2] * ls[2])
        return r, p, s

    def walk(n, rot, pos, scl):
        r, p, s = compose(n, rot, pos, scl)
        mb = mesh_bounds.get(n["mesh"]) if n["mesh"] is not None else None
        if mb is not None:
            for cx in (mb[0][0], mb[1][0]):
                for cy in (mb[0][1], mb[1][1]):
                    for cz in (mb[0][2], mb[1][2]):
                        rv = qrot(r, (cx * s[0], cy * s[1], cz * s[2]))
                        out.append((p[0] + rv[0], p[1] + rv[1], p[2] + rv[2]))
        for c in n["children"]:
            walk(c, r, p, s)

    for n in nodes:
        walk(n, (0.0, 0.0, 0.0, 1.0), (0.0, 0.0, 0.0), (1.0, 1.0, 1.0))
    return out


def authoring_unit_scale(src, cosmetics):
    """Recover the unit scale of the scene the cosmetics were authored in.

    MoreCompany's prefabs were authored on a scaled clone of the Lethal Company rig: their
    internal proportions are only consistent if every length is ~2.1x the LC model's own
    units (e.g. the party hat's brim sits at y=0.796 where the LC head top is 0.370). The
    scale is measured here from the cosmetics that visibly rest on top of the head (their
    lowest geometry point == the head top), as the median of lowest_point/head_top.
    Nothing from MoreCompany's runtime 0.38 fudge is used; the target fit divides by this
    measured authoring scale and multiplies by Alterum's measured head size.
    """
    ratios = []
    for c in cosmetics:
        a = c.get("aabb")
        if not a or c["type"] != 0:
            continue
        low = a["min"][1]
        if low <= src["top"]:  # geometry starts below the head top -> face/ear item
            continue
        ratios.append(low / src["top"])
    ratios.sort()
    if not ratios:
        return 1.0
    n = len(ratios)
    median = ratios[n // 2] if n % 2 else 0.5 * (ratios[n // 2 - 1] + ratios[n // 2])
    return median


def scale_box(box, u):
    """Convert every *length* in an authoring-unit box to the source-rig unit.

    Ratios of two lengths (widthPerNeckToHead, topPerNeckToHead) are dimensionless and must stay
    unit-invariant: scaling them too made the unreadable-mesh fallback build a target head box
    `authoringUnitScale` times too large (~2.06x for this pack).
    """
    out = dict(box)
    for k in ("min", "max", "size", "center"):
        if k in out:
            out[k] = [round(v * u, 6) for v in out[k]]
    for k in ("width", "top", "centerX", "centerZ", "neckToHeadDistance"):
        if k in out:
            out[k] = round(out[k] * u, 6)
    return out


def main():
    env, objs, trees = load_bundle(COSMETICS_BUNDLE)
    scripts = {pid: (trees[pid] or {}).get("m_ClassName")
               for pid, o in objs.items() if o.type.name == "MonoScript"}
    transforms = {pid: d for pid, d in trees.items() if objs[pid].type.name == "Transform"}
    gos = {pid: d for pid, d in trees.items() if objs[pid].type.name == "GameObject"}
    go_comps = {}
    for pid, d in trees.items():
        if not d:
            continue
        go = d.get("m_GameObject")
        if isinstance(go, dict) and go.get("m_PathID"):
            go_comps.setdefault(go["m_PathID"], []).append(pid)

    # ---------------------------------------------------------------- materials
    material_ids = set()
    for pid, d in trees.items():
        if objs[pid].type.name != "MeshRenderer":
            continue
        for m in d.get("m_Materials") or []:
            material_ids.add(m["m_PathID"])

    materials = {}
    texture_ids = set()
    for mid in sorted(material_ids):
        d = trees[mid]
        sp = d.get("m_SavedProperties") or {}
        tex = {}
        for p in sp.get("m_TexEnvs") or []:
            t = p[1].get("m_Texture") or {}
            if t.get("m_PathID"):
                tex[p[0]] = t["m_PathID"]
        cols = {}
        for p in sp.get("m_Colors") or []:
            v = p[1]
            cols[p[0]] = [v.get(k, 0.0) for k in ("r", "g", "b", "a")]
        fl = {p[0]: p[1] for p in sp.get("m_Floats") or []}
        base = cols.get("_BaseColor") or cols.get("_Color") or [1, 1, 1, 1]
        # HDRP/Lit: _BaseColorMap albedo, _NormalMap normal, _MaskMap (R metal, A smooth, G ao)
        basemap = tex.get("_BaseColorMap") or tex.get("_MainTex") or tex.get("_BaseTexture")
        normalmap = tex.get("_NormalMap")
        maskmap = tex.get("_MaskMap") or tex.get("_MaskMapTexture")
        emissive = tex.get("_EmissiveColorMap")
        for t in (basemap, normalmap, maskmap, emissive):
            if t:
                texture_ids.add(t)
        materials[mid] = {
            "name": d.get("m_Name"),
            "baseColor": [round(c, 6) for c in base],
            "metallic": float(fl.get("_Metallic", 0.0)),
            "smoothness": float(fl.get("_Smoothness", 0.5)),
            "transparent": float(fl.get("_SurfaceType", 0.0)) != 0.0 or float(fl.get("_BlendMode", 0.0)) != 0.0,
            "doubleSided": float(fl.get("_DoubleSidedEnable", 0.0)) != 0.0,
            "baseMap": basemap,
            "normalMap": normalmap,
            "maskMap": maskmap,
            "emissiveMap": emissive,
            "emissiveColor": [round(c, 6) for c in (cols.get("_EmissiveColor") or [0, 0, 0, 1])],
        }

    # ---------------------------------------------------------------- textures
    blob = Blob()
    textures = {}
    tex_report = []
    for tid in sorted(texture_ids):
        t = objs[tid].read()
        img = t.image  # decodes DXT1/5 + crunched + BC7 to RGBA
        if img.mode != "RGBA":
            img = img.convert("RGBA")
        if TEX_MAX and max(img.size) > TEX_MAX:
            img.thumbnail((TEX_MAX, TEX_MAX))
        buf = io.BytesIO()
        img.save(buf, format="PNG", compress_level=9)
        png = buf.getvalue()
        entry = blob.add(png, 4)
        entry["w"] = img.size[0]
        entry["h"] = img.size[1]
        entry["name"] = t.m_Name
        entry["sourceFormat"] = int(t.m_TextureFormat)
        entry["sourceW"] = t.m_Width
        entry["sourceH"] = t.m_Height
        entry["filter"] = int(t.m_TextureSettings.m_FilterMode)
        entry["wrapU"] = int(t.m_TextureSettings.m_WrapU)
        entry["wrapV"] = int(t.m_TextureSettings.m_WrapV)
        textures[tid] = entry
        tex_report.append((t.m_Name, t.m_Width, t.m_Height, len(png)))

    # ---------------------------------------------------------------- meshes
    mesh_ids = set()
    for pid, d in trees.items():
        if objs[pid].type.name != "MeshFilter":
            continue
        mesh_ids.add(d["m_Mesh"]["m_PathID"])

    meshes = {}
    mesh_report = []
    for mid in sorted(mesh_ids):
        o = objs[mid]
        mesh = o.read()
        td = o.read_typetree()
        handler = MeshHandler(mesh)
        handler.process()
        verts = handler.m_Vertices or []
        if not verts:
            raise SystemExit(f"mesh {mid} {mesh.m_Name}: no vertex data")
        normals = handler.m_Normals or []
        tangents = handler.m_Tangents or []
        uvs = handler.m_UV0 or []
        indices = handler.m_IndexBuffer or []
        submeshes = td["m_SubMeshes"]
        # Flatten: resolve each submesh's firstByte/baseVertex into one index list.
        flat = []
        sub_out = []
        raw = list(indices)
        for s in submeshes:
            first = s["firstByte"] // 2 if s["indexCount"] and len(raw) * 2 >= s["firstByte"] + s["indexCount"] * 2 else s["firstByte"] // 2
            count = s["indexCount"]
            base = s["baseVertex"]
            piece = [raw[i] + base for i in range(first, first + count)]
            sub_out.append({"indexStart": len(flat), "indexCount": count, "topology": s["topology"]})
            flat.extend(piece)
        if not sub_out:
            sub_out = [{"indexStart": 0, "indexCount": len(raw), "topology": 0}]
            flat = raw
        max_index = max(flat) if flat else 0
        entry = {
            "name": mesh.m_Name,
            "vertexCount": len(verts),
            "subMeshes": sub_out,
            "indexFormat": 32 if max_index > 65535 else 16,
            "boundsMin": [min(v[k] for v in verts) for k in range(3)],
            "boundsMax": [max(v[k] for v in verts) for k in range(3)],
            "hasUV0": len(uvs) == len(verts),
            "hasNormals": len(normals) == len(verts),
            "hasTangents": len(tangents) == len(verts),
        }
        vbuf = bytearray()
        for v in verts:
            vbuf += struct.pack("<3f", *v)
        entry["vertices"] = blob.add(bytes(vbuf), 4)
        if entry["hasNormals"]:
            nbuf = bytearray()
            for n in normals:
                nbuf += struct.pack("<3f", n[0], n[1], n[2])
            entry["normals"] = blob.add(bytes(nbuf), 4)
        if entry["hasTangents"]:
            tbuf = bytearray()
            for t in tangents:
                tbuf += struct.pack("<4f", t[0], t[1], t[2], t[3])
            entry["tangents"] = blob.add(bytes(tbuf), 4)
        if entry["hasUV0"]:
            ubuf = bytearray()
            for u in uvs:
                ubuf += struct.pack("<2f", u[0], u[1])
            entry["uv0"] = blob.add(bytes(ubuf), 4)
        ibuf = bytearray()
        for i in flat:
            ibuf += struct.pack("<I", i)
        entry["indices"] = blob.add(bytes(ibuf), 4)
        meshes[mid] = entry
        mesh_report.append((mesh.m_Name, len(verts), len(flat)))

    # ---------------------------------------------------------------- cosmetics
    def tr_name(tpid):
        return gos[transforms[tpid]["m_GameObject"]["m_PathID"]]["m_Name"]

    def walk(tpid, out):
        d = transforms[tpid]
        gp = d["m_GameObject"]["m_PathID"]
        node = {
            "name": gos[gp]["m_Name"],
            "active": bool(gos[gp].get("m_IsActive", True)),
            "pos": [d["m_LocalPosition"][k] for k in "xyz"],
            "rot": [d["m_LocalRotation"][k] for k in "xyzw"],
            "scale": [d["m_LocalScale"][k] for k in "xyz"],
            "mesh": None,
            "materials": [],
            "children": [],
        }
        for c in go_comps.get(gp, []):
            kind = objs[c].type.name
            if kind == "MeshFilter":
                node["mesh"] = trees[c]["m_Mesh"]["m_PathID"]
            elif kind == "MeshRenderer":
                node["materials"] = [m["m_PathID"] for m in trees[c].get("m_Materials") or []]
        for ch in d.get("m_Children") or []:
            node["children"].append(walk(ch["m_PathID"], []))
        out.append(node)
        return node

    cosmetics = []
    for pid, d in trees.items():
        if objs[pid].type.name != "MonoBehaviour":
            continue
        s = (d or {}).get("m_Script") or {}
        if scripts.get(s.get("m_PathID")) != "CosmeticInstance":
            continue
        gop = d["m_GameObject"]["m_PathID"]
        tpid = next(tp for tp, tdd in transforms.items()
                    if tdd["m_GameObject"]["m_PathID"] == gop)
        nodes = []
        walk(tpid, nodes)
        root = nodes[0]
        mesh_bounds = {mid: (m["boundsMin"], m["boundsMax"]) for mid, m in meshes.items()}
        pts = aabb_in_root(root["children"], mesh_bounds)
        if pts:
            aabb = {"min": [min(p[k] for p in pts) for k in range(3)],
                    "max": [max(p[k] for p in pts) for k in range(3)]}
        else:
            aabb = None
        # The prefab root carries an authoring-scene TRS that MoreCompany overwrites at
        # runtime for position/rotation, but its *scale* it multiplies into the runtime scale
        # (transform.localScale *= 0.38f). So rootAuthoring.scale is part of the cosmetic's
        # authored size (1.2 on four of them) and the runtime applies it exactly once;
        # position/rotation stay audit-only.
        cosmetics.append({
            "id": d.get("cosmeticId"),
            "type": int(d.get("cosmeticType", 0)),
            "icon": d.get("icon", {}).get("m_PathID"),
            "prefab": root["name"],
            "rootAuthoring": {"pos": root["pos"], "rot": root["rot"], "scale": root["scale"]},
            "aabb": aabb,
            "children": root["children"],
        })
    cosmetics.sort(key=lambda c: c["id"])

    # ------------------------------------------- source head reference (display model)
    head = extract_source_head()
    u = authoring_unit_scale(head["bounds"], cosmetics)
    head["bounds"] = scale_box(head["bounds"], u)
    head["bounds"]["authoringUnitScale"] = round(u, 6)

    manifest = {
        "schema": 1,
        "generator": "extract_cosmetics.py (UnityPy %s)" % UnityPy.__version__,
        "source": {
            "mod": "notnotnotswipez-MoreCompany-1.14.0",
            "cosmeticsBundle": {"file": os.path.basename(COSMETICS_BUNDLE),
                                "sha256": sha256(COSMETICS_BUNDLE)},
            "assetsBundle": {"file": os.path.basename(ASSETS_BUNDLE),
                             "sha256": sha256(ASSETS_BUNDLE)},
        },
        "coordinate": {
            "space": "source head-bone local (Unity left-handed, +Y up along the head)",
            "rowOrder": "texture PNG top-down as decoded by UnityPy",
            "headRotationRelativeToModelRoot": head["rotRelModel"],
        },
        "sourceHead": head["bounds"],
        "textures": {str(k): v for k, v in textures.items()},
        "materials": {str(k): v for k, v in materials.items()},
        "meshes": {str(k): v for k, v in meshes.items()},
        "cosmetics": cosmetics,
        "counts": {
            "cosmetics": len(cosmetics),
            "meshes": len(meshes),
            "materials": len(materials),
            "textures": len(textures),
        },
    }

    mjson = json.dumps(manifest, separators=(",", ":")).encode("utf-8")
    os.makedirs(os.path.dirname(OUT_PACK), exist_ok=True)
    with open(OUT_PACK, "wb") as f:
        f.write(b"ACPK1\0\0\0")
        f.write(struct.pack("<I", len(mjson)))
        f.write(mjson)
        while f.tell() % 4:
            f.write(b"\0")
        f.write(blob.buf)

    os.makedirs(os.path.dirname(OUT_AUDIT), exist_ok=True)
    audit = {
        "counts": manifest["counts"],
        "packBytes": os.path.getsize(OUT_PACK),
        "manifestBytes": len(mjson),
        "blobBytes": len(blob.buf),
        "sourceHead": head,
        "cosmetics": [{"id": c["id"], "type": c["type"], "prefab": c["prefab"],
                       "rootScale": c["rootAuthoring"]["scale"],
                       "children": len(c["children"])} for c in cosmetics],
        "textures": [{"name": n, "w": w, "h": h, "pngBytes": b} for n, w, h, b in tex_report],
        "meshes": [{"name": n, "verts": v, "indices": i} for n, v, i in mesh_report],
    }
    with open(OUT_AUDIT, "w", encoding="utf-8") as f:
        json.dump(audit, f, indent=1)

    print(f"cosmetics={len(cosmetics)} meshes={len(meshes)} materials={len(materials)} textures={len(textures)}")
    print(f"pack {OUT_PACK} {os.path.getsize(OUT_PACK)/1e6:.2f} MB "
          f"(manifest {len(mjson)/1e3:.1f} kB, blobs {len(blob.buf)/1e6:.2f} MB)")
    print(f"source head bounds {head['bounds']}")


def extract_source_head():
    """Head-weighted vertex bounds of Lethal Company's own player rig, measured in the
    spine.004 (head) bone's local space through the skinned mesh bindposes. This is the
    anatomical reference the MoreCompany offsets were authored against."""
    env, objs, trees = load_bundle(ASSETS_BUNDLE)
    transforms = {pid: d for pid, d in trees.items()
                  if objs[pid].type.name in ("Transform", "RectTransform")}
    gos = {pid: d for pid, d in trees.items() if objs[pid].type.name == "GameObject"}

    def nm(p):
        return gos[transforms[p]["m_GameObject"]["m_PathID"]]["m_Name"]

    def chain(p):
        out = []
        while p:
            out.append(p)
            p = (transforms[p].get("m_Father") or {}).get("m_PathID", 0)
        return out

    def world_rot(p):
        q = (0.0, 0.0, 0.0, 1.0)
        for t in reversed(chain(p)):
            lr = transforms[t]["m_LocalRotation"]
            q = qmul(q, (lr["x"], lr["y"], lr["z"], lr["w"]))
        return q

    scavenger = next(p for p in transforms if nm(p) == "ScavengerModel")
    head_bone = next(p for p in transforms if nm(p) == "spine.004")
    neck_bone = next(p for p in transforms if nm(p) == "spine.003")
    rot_rel = qmul(qinv(world_rot(scavenger)), world_rot(head_bone))

    smr = next(d for pid, d in trees.items()
               if objs[pid].type.name == "SkinnedMeshRenderer"
               and (trees.get(d["m_GameObject"]["m_PathID"], {}) or {}).get("m_Name") == "LOD1")
    mesh_id = smr["m_Mesh"]["m_PathID"]
    mesh = objs[mesh_id].read()
    handler = MeshHandler(mesh)
    handler.process()
    bones = [b["m_PathID"] for b in smr["m_Bones"]]
    head_index = bones.index(next(b for b in bones if nm(b) == "spine.004"))
    bp = mesh.m_BindPose[head_index]
    m = [[bp.e00, bp.e01, bp.e02, bp.e03],
         [bp.e10, bp.e11, bp.e12, bp.e13],
         [bp.e20, bp.e21, bp.e22, bp.e23]]
    mins = [1e9] * 3
    maxs = [-1e9] * 3
    count = 0
    for vi, v in enumerate(handler.m_Vertices):
        bi = handler.m_BoneIndices[vi]
        bw = handler.m_BoneWeights[vi]
        w = sum(bw[k] for k in range(4) if bi[k] == head_index)
        if w < 0.5:
            continue
        p = [m[r][0] * v[0] + m[r][1] * v[1] + m[r][2] * v[2] + m[r][3] for r in range(3)]
        # Express in the cosmetic's frame: R_src^-1 * p (see manifest.coordinate).
        p = list(qrot(qinv(rot_rel), p))
        count += 1
        for k in range(3):
            mins[k] = min(mins[k], p[k])
            maxs[k] = max(maxs[k], p[k])

    # neck->head bone distance in the same (metarig-local, scale-1) units, for the
    # runtime fallback when the target mesh is not CPU readable.
    def rel_pos(p):
        c = list(reversed(chain(p)[:chain(p).index(next(x for x in transforms if nm(x) == "metarig"))]))
        pos = [0.0, 0.0, 0.0]
        q = (0.0, 0.0, 0.0, 1.0)
        for t in c:
            d = transforms[t]
            lp = d["m_LocalPosition"]
            ls = d["m_LocalScale"]
            pv = (lp["x"] * ls["x"], lp["y"] * ls["y"], lp["z"] * ls["z"])
            rv = qrot(q, pv)
            pos = [pos[0] + rv[0], pos[1] + rv[1], pos[2] + rv[2]]
            lr = d["m_LocalRotation"]
            q = qmul(q, (lr["x"], lr["y"], lr["z"], lr["w"]))
        return pos

    hp = rel_pos(head_bone)
    np_ = rel_pos(neck_bone)
    neck_dist = sum((hp[k] - np_[k]) ** 2 for k in range(3)) ** 0.5

    bounds = {
        "space": "spine.004 local",
        "vertices": count,
        "min": [round(v, 6) for v in mins],
        "max": [round(v, 6) for v in maxs],
        "size": [round(maxs[k] - mins[k], 6) for k in range(3)],
        "center": [round((maxs[k] + mins[k]) * 0.5, 6) for k in range(3)],
        "width": round(maxs[0] - mins[0], 6),
        "top": round(maxs[1], 6),
        "centerX": round((maxs[0] + mins[0]) * 0.5, 6),
        "centerZ": round((maxs[2] + mins[2]) * 0.5, 6),
        "neckToHeadDistance": round(neck_dist, 6),
        "widthPerNeckToHead": round((maxs[0] - mins[0]) / neck_dist, 6) if neck_dist else 0.0,
        "topPerNeckToHead": round(maxs[1] / neck_dist, 6) if neck_dist else 0.0,
    }
    return {"bounds": bounds,
            "rotRelModel": [round(v, 6) for v in rot_rel]}


if __name__ == "__main__":
    sys.exit(main())
