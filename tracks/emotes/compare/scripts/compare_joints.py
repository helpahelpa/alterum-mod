#!/usr/bin/env python3
"""Rank reference-vs-port canonical joint errors.

Reference: compare/ref/<clip>.csv (LC rig + authored curves + LC's own arm/leg IK).
Port:      compare/port/<clip>_<tag>.csv (one row per rendered frame, world joints).

Metrics, all in the body frame (pelvis origin, model-root orientation):
  pos*     joint position error (body frame), raw cm and / body height
  seg*     parent->child segment direction error (twist-free pose)
  dq*      rest-relative global rotation error (twist-free motion; separates retarget
           rest-convention bias from actual pose error)
  pelvis   animated pelvis translation range per axis (source vs port)
The absolute rotation is also reported as `rot*`, but it includes the different bone-axis
conventions of the two rigs and must not be ranked.
"""
import argparse, csv, json, math, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
TRACK = os.path.normpath(os.path.join(HERE, "..", ".."))
REF = os.path.join(TRACK, "compare", "ref")
PORT = os.path.join(TRACK, "compare", "port")
OUT = os.path.join(TRACK, "compare")
CLIPS = ["Clap", "D_Clap", "D_Middle_Finger", "D_Sign", "Idle1Fix", "Middle_Finger",
         "Prisyadka", "Salute", "Shy", "Sign", "The_Griddy", "Twerk"]
JOINTS = ["pelvis", "spine_01", "spine_02", "spine_03", "neck_01", "head",
          "clavicle_l", "upperarm_l", "lowerarm_l", "hand_l",
          "clavicle_r", "upperarm_r", "lowerarm_r", "hand_r",
          "thigh_l", "calf_l", "foot_l", "ball_l",
          "thigh_r", "calf_r", "foot_r", "ball_r"]
SEG = {"pelvis": "spine_01", "spine_01": "spine_02", "spine_02": "spine_03",
       "spine_03": "neck_01", "neck_01": "head",
       "clavicle_l": "upperarm_l", "upperarm_l": "lowerarm_l", "lowerarm_l": "hand_l",
       "clavicle_r": "upperarm_r", "upperarm_r": "lowerarm_r", "lowerarm_r": "hand_r",
       "thigh_l": "calf_l", "calf_l": "foot_l", "foot_l": "ball_l",
       "thigh_r": "calf_r", "calf_r": "foot_r", "foot_r": "ball_r"}
BODY_H = 1.70

def qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return (aw*bx + ax*bw + ay*bz - az*by, aw*by - ax*bz + ay*bw + az*bx,
            aw*bz + ax*by - ay*bx + az*bw, aw*bw - ax*bx - ay*by - az*bz)
def qconj(q): return (-q[0], -q[1], -q[2], q[3])
def qnorm(q):
    n = math.sqrt(sum(c*c for c in q)); return tuple(c/n for c in q) if n > 1e-12 else (0,0,0,1)
def qrot(q, v):
    x,y,z,w=q; vx,vy,vz=v
    tx=2*(y*vz-z*vy); ty=2*(z*vx-x*vz); tz=2*(x*vy-y*vx)
    return (vx+w*tx+(y*tz-z*ty), vy+w*ty+(z*tx-x*tz), vz+w*tz+(x*ty-y*tx))
def qang(a, b):
    d=abs(sum(x*y for x,y in zip(a,b)))
    return math.degrees(2*math.acos(max(-1.0,min(1.0,d))))
def vsub(a,b): return tuple(x-y for x,y in zip(a,b))
def vadd(a,b): return tuple(x+y for x,y in zip(a,b))
def vmul(a,s): return tuple(x*s for x in a)
def vlen(v): return math.sqrt(sum(c*c for c in v))
def vnorm(v):
    n=vlen(v); return tuple(x/n for x in v) if n>1e-9 else (0,0,0)
def vang(a,b):
    na,nb=vnorm(a),vnorm(b)
    return math.degrees(math.acos(max(-1.0,min(1.0,sum(x*y for x,y in zip(na,nb))))))

def load_csv(path):
    rows=[]
    with open(path) as f:
        r=csv.DictReader(f)
        for row in r:
            t=float(row["t"])
            fade=float(row.get("fade", 1.0)) if row.get("fade") not in (None, "") else 1.0
            rootp=(float(row["root_px"]),float(row["root_py"]),float(row["root_pz"]))
            rootq=qnorm((float(row["root_qx"]),float(row["root_qy"]),float(row["root_qz"]),float(row["root_qw"])))
            j={}
            for n in JOINTS:
                j[n]=((float(row[n+"_px"]),float(row[n+"_py"]),float(row[n+"_pz"])),
                      qnorm((float(row[n+"_qx"]),float(row[n+"_qy"]),float(row[n+"_qz"]),float(row[n+"_qw"]))))
            rows.append((t,rootp,rootq,j,fade))
    return rows

def parse_log(path):
    basis=None; bind={}
    try: txt=open(path, errors="replace").read()
    except Exception: return basis, bind
    m=re.findall(r"\[emote:rig\] basis quat=\(([-0-9.]+),([-0-9.]+),([-0-9.]+),([-0-9.]+)\)", txt)
    if m: basis=tuple(float(x) for x in m[-1])
    for line in reversed(txt.splitlines()):
        if "[emote:rig] bind joints" in line:
            for mm in re.finditer(r"(\w+)=\(([-0-9.]+),([-0-9.]+),([-0-9.]+)\)\(([-0-9.]+),([-0-9.]+),([-0-9.]+),([-0-9.]+)\)", line):
                bind[mm.group(1)]=((float(mm.group(2)),float(mm.group(3)),float(mm.group(4))),
                                   qnorm((float(mm.group(5)),float(mm.group(6)),float(mm.group(7)),float(mm.group(8)))))
            break
    return basis, bind

def main():
    ap=argparse.ArgumentParser()
    ap.add_argument("--tag", default="fixed")
    ap.add_argument("--clips", default=",".join(CLIPS))
    ap.add_argument("--log", default="/mnt/c/AlterumT2/BepInEx/LogOutput.log")
    ap.add_argument("--json", default=os.path.join(OUT, "errors.json"))
    args=ap.parse_args()
    basis, bind = parse_log(args.log)
    if basis is None: basis=(0,0,0,1)
    refmeta=json.load(open(os.path.join(REF,"ref_meta.json")))
    # source rest world rotations for the rest-relative metric
    sys.path.insert(0, HERE)
    import ref_eval_RETIRED as RE   # RETIRED: half-scale, arm pose wrong; see compare_vs_lc/EVIDENCE.txt
    rig=RE.Rig()
    grest,_p,_l=RE.fk(rig,{},0.0)
    Rsrc=grest[""]
    refLeg=refmeta["source_rest"]["leg_sum_l"]
    portLeg=(vlen(vsub(bind["thigh_l"][0], bind["calf_l"][0])) +
             vlen(vsub(bind["calf_l"][0], bind["foot_l"][0]))) if "thigh_l" in bind else refLeg
    S=portLeg/refLeg if refLeg>1e-6 else 1.0
    portH=(vlen(vsub(bind["head"][0], bind["ball_l"][0])) if "head" in bind and "ball_l" in bind else BODY_H)
    print(f"basis={tuple(round(x,5) for x in basis)} refLeg={refLeg:.4f} portLeg={portLeg:.4f} S={S:.4f} portHeight={portH:.3f}")
    all_stats={}
    for clip in args.clips.split(","):
        ref=load_csv(os.path.join(REF, clip+".csv"))
        ppath=os.path.join(PORT, f"{clip}_{args.tag}.csv")
        if not os.path.exists(ppath):
            print("  missing", ppath); continue
        port=load_csv(ppath)
        used=[r for r in port if r[0] >= 0.30 and (len(r) < 5 or r[4] >= 0.99)] or [r for r in port if len(r) < 5 or r[4] >= 0.99] or port
        ref_rest_pel = RE.vmul(RE.qrot(Rsrc, RE.vsub(_p["spine"], _p[""])), 1.0)
        # rest pelvis world in source model units (already includes metarig scale)
        stats={"frames":len(used),"scale":S,"joints":{},"pelvis":{}}
        acc={n:{"pos":[],"seg":[],"dq":[],"rot":[]} for n in JOINTS}
        pel_ref=[]; pel_pt=[]
        for row in used:
            t,rp,rq,pj=row[0],row[1],row[2],row[3]
            idx=max(0,min(len(ref)-1,int(round(t*60.0))))
            _,rrp,rrq,rj=ref[idx][:4]
            # reference body frame: source model positions mapped by C, pelvis-relative, and
            # rotated into the port's current root frame (root yaw cancels on both sides)
            C=basis
            pp_ref = vmul(qrot(C, rj["pelvis"][0]), S)
            for n in JOINTS:
                p_ref = vmul(qrot(C, rj[n][0]), S)
                q_ref = qnorm(qmul(qmul(C, rj[n][1]), qconj(C)))
                # body frame = pelvis origin, root orientation of the port
                pos_r = qrot(qconj(rq), vsub(p_ref, pp_ref))
                pos_p = qrot(qconj(rq), vsub(pj[n][0], pj["pelvis"][0]))
                acc[n]["pos"].append(vlen(vsub(pos_r,pos_p)))
                # segment direction error
                if n in SEG:
                    ch=SEG[n]
                    pr2 = vmul(qrot(C, rj[ch][0]), S)
                    pp2 = pj[ch][0]
                    d_r = vsub(pos_r, qrot(qconj(rq), vsub(pr2, pp_ref)))
                    d_p = vsub(pos_p, qrot(qconj(rq), vsub(pp2, pj["pelvis"][0])))
                    acc[n]["seg"].append(vang(d_r,d_p))
                # Rest-corrected absolute pose: the retarget's own target orientation is
                #   q_expected = C * (q_ref * q_rest^-1) * C^-1 * anchor
                # (source pose delta from its rest, applied to this model's rest anchor). This
                # cancels both rigs' bone-axis conventions and is the correct "did the pose
                # transfer" metric. q_actual is the port's root-local global.
                anc = bind.get(n, (None,None))[1]
                if anc is not None:
                    d_src = qnorm(qmul(q_ref, qconj(grest[RE.PATH_OF[n]])))
                    q_exp = qnorm(qmul(qmul(qmul(C, d_src), qconj(C)), anc))
                    q_act = qnorm(qmul(qconj(rq), pj[n][1]))
                    acc[n]["dq"].append(qang(q_exp, q_act))
                acc[n]["rot"].append(qang(qmul(qconj(rq), q_ref), qmul(qconj(rq), pj[n][1])))
            # pelvis translation deviation from rest, body frame
            pel_r = qrot(qconj(rq), vmul(qrot(C, vsub(rj["pelvis"][0], ref_rest_pel)), S))
            pel_ref.append(pel_r)
            # port: pelvis world - rest pelvis world = q_port - (root_pos + root_rot*anchor_pelvis)
            anc_p = bind.get("pelvis", (None,None))[0]
            if anc_p is not None:
                rest_world = vadd(rp, qrot(rq, anc_p))
                pel_pt.append(qrot(qconj(rq), vsub(pj["pelvis"][0], rest_world)))
        # pelvis ranges
        def rng(v, i):
            vals=[x[i] for x in v]
            return max(vals)-min(vals), sum(vals)/len(vals)
        pr=[rng(pel_ref,i) for i in range(3)]
        pp=[rng(pel_pt,i) for i in range(3)]
        stats["pelvis"]={"ref_range":[round(x[0],4) for x in pr],"port_range":[round(x[0],4) for x in pp],
                         "ref_mean":[round(x[1],4) for x in pr],"port_mean":[round(x[1],4) for x in pp],
                         "range_err":[round(abs(pr[i][0]-pp[i][0]),4) for i in range(3)]}
        for n in JOINTS:
            a=acc[n]
            stats["joints"][n]={
                "pos_mean":sum(a["pos"])/len(a["pos"]), "pos_max":max(a["pos"]),
                "pos_mean_norm":sum(a["pos"])/len(a["pos"])/portH, "pos_max_norm":max(a["pos"])/portH,
                "seg_mean":(sum(a["seg"])/len(a["seg"])) if a["seg"] else 0.0,
                "seg_max":max(a["seg"]) if a["seg"] else 0.0,
                "dq_mean":(sum(a["dq"])/len(a["dq"])) if a["dq"] else 0.0,
                "dq_max":max(a["dq"]) if a["dq"] else 0.0,
                "dq_min":min(a["dq"]) if a["dq"] else 0.0,
                "rot_mean":sum(a["rot"])/len(a["rot"]), "rot_max":max(a["rot"]),
            }
        stats["body_height"]=portH
        all_stats[clip]=stats
        wp=sorted([n for n in JOINTS if n!="head"],key=lambda n:-stats["joints"][n]["pos_mean"])[:6]
        ws=sorted([n for n in JOINTS if stats["joints"][n]["seg_mean"]>0],key=lambda n:-stats["joints"][n]["seg_mean"])[:6]
        wd=sorted(JOINTS,key=lambda n:-stats["joints"][n]["dq_mean"])[:6]
        print(f"\n{clip} frames={len(used)} pelvis ranges ref={stats['pelvis']['ref_range']} port={stats['pelvis']['port_range']} err={stats['pelvis']['range_err']}")
        print("  worst pos cm:  ", ", ".join(f"{n}={stats['joints'][n]['pos_mean']*100:.1f}" for n in wp))
        print("  worst seg deg: ", ", ".join(f"{n}={stats['joints'][n]['seg_mean']:.1f}" for n in ws))
        print("  worst dq deg:  ", ", ".join(f"{n}={stats['joints'][n]['dq_mean']:.1f}" for n in wd))
    json.dump(all_stats, open(args.json,"w"), indent=1)
    print("\nwrote", args.json)

if __name__=="__main__":
    main()
