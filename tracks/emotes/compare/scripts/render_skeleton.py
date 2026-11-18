#!/usr/bin/env python3
"""Render side-by-side stick-figure comparisons (reference vs port) with PIL.

Body frame: pelvis origin, model-root orientation, target units. Front = X/Y, side = Z/Y.
"""
import argparse, csv, json, math, os, sys
from PIL import Image, ImageDraw, ImageFont

HERE=os.path.dirname(os.path.abspath(__file__))
TRACK=os.path.normpath(os.path.join(HERE,"..",".."))
REF=os.path.join(TRACK,"compare","ref"); PORT=os.path.join(TRACK,"compare","port")
OUT=os.path.join(TRACK,"compare","sheets")
JOINTS=["pelvis","spine_01","spine_02","spine_03","neck_01","head",
        "clavicle_l","upperarm_l","lowerarm_l","hand_l","clavicle_r","upperarm_r","lowerarm_r","hand_r",
        "thigh_l","calf_l","foot_l","ball_l","thigh_r","calf_r","foot_r","ball_r"]
BONES=[("pelvis","spine_01"),("spine_01","spine_02"),("spine_02","spine_03"),("spine_03","neck_01"),("neck_01","head"),
       ("spine_03","clavicle_l"),("clavicle_l","upperarm_l"),("upperarm_l","lowerarm_l"),("lowerarm_l","hand_l"),
       ("spine_03","clavicle_r"),("clavicle_r","upperarm_r"),("upperarm_r","lowerarm_r"),("lowerarm_r","hand_r"),
       ("pelvis","thigh_l"),("thigh_l","calf_l"),("calf_l","foot_l"),("foot_l","ball_l"),
       ("pelvis","thigh_r"),("thigh_r","calf_r"),("calf_r","foot_r"),("foot_r","ball_r")]
SEG={"pelvis":"spine_01","spine_01":"spine_02","spine_02":"spine_03","spine_03":"neck_01","neck_01":"head",
     "clavicle_l":"upperarm_l","upperarm_l":"lowerarm_l","lowerarm_l":"hand_l",
     "clavicle_r":"upperarm_r","upperarm_r":"lowerarm_r","lowerarm_r":"hand_r",
     "thigh_l":"calf_l","calf_l":"foot_l","foot_l":"ball_l","thigh_r":"calf_r","calf_r":"foot_r","foot_r":"ball_r"}

def qrot(q,v):
    x,y,z,w=q; vx,vy,vz=v
    tx=2*(y*vz-z*vy); ty=2*(z*vx-x*vz); tz=2*(x*vy-y*vx)
    return (vx+w*tx+(y*tz-z*ty), vy+w*ty+(z*tx-x*tz), vz+w*tz+(x*ty-y*tx))
def qconj(q): return (-q[0],-q[1],-q[2],q[3])
def vsub(a,b): return tuple(x-y for x,y in zip(a,b))
def vlen(v): return math.sqrt(sum(c*c for c in v))
def load(path):
    rows=[]
    with open(path) as f:
        for row in csv.DictReader(f):
            t=float(row["t"]); rq=(float(row["root_qx"]),float(row["root_qy"]),float(row["root_qz"]),float(row["root_qw"]))
            j={n:((float(row[n+"_px"]),float(row[n+"_py"]),float(row[n+"_pz"])),
                  (float(row[n+"_qx"]),float(row[n+"_qy"]),float(row[n+"_qz"]),float(row[n+"_qw"]))) for n in JOINTS}
            rows.append((t,rq,j))
    return rows

def parse_log(path):
    basis=None
    txt=open(path,errors="replace").read()
    import re
    m=re.findall(r"\[emote:rig\] basis quat=\(([-0-9.]+),([-0-9.]+),([-0-9.]+),([-0-9.]+)\)",txt)
    if m: basis=tuple(float(x) for x in m[-1])
    return basis

def font(sz):
    for p in ("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf","/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf"):
        if os.path.exists(p): return ImageFont.truetype(p,sz)
    return ImageFont.load_default()

def main():
    ap=argparse.ArgumentParser()
    ap.add_argument("--clip",default="Clap")
    ap.add_argument("--tag",default="fixed")
    ap.add_argument("--log",default="/mnt/c/AlterumT2/BepInEx/LogOutput.log")
    ap.add_argument("--phases",default="0.15,0.3,0.45,0.6,0.75,0.9")
    ap.add_argument("--out",default=None)
    args=ap.parse_args()
    C=parse_log(args.log) or (0,0,0,1)
    ref=load(os.path.join(REF,args.clip+".csv"))
    port=load(os.path.join(PORT,f"{args.clip}_{args.tag}.csv"))
    # scale
    meta=json.load(open(os.path.join(REF,"ref_meta.json")))
    refLeg=meta["source_rest"]["leg_sum_l"]
    # port leg from first row
    p0=port[0]
    portLeg=vlen(vsub(p0[2]["thigh_l"][0],p0[2]["calf_l"][0]))+vlen(vsub(p0[2]["calf_l"][0],p0[2]["foot_l"][0]))
    S=portLeg/refLeg
    phases=[float(x) for x in args.phases.split(",")]
    dur=ref[-1][0]
    picks=[]
    for ph in phases:
        t=ph*dur
        ri=min(range(len(ref)),key=lambda i:abs(ref[i][0]-t))
        cand=[r for r in port if r[0]>=0.30]
        pi=min(range(len(cand)),key=lambda i:abs(cand[i][0]-t))
        picks.append((ph,t,ref[ri],cand[pi]))
    W,H=1500,330*max(1,len(picks))+90
    img=Image.new("RGB",(W,H),(18,18,22)); d=ImageDraw.Draw(img)
    f=font(15); fb=font(19); ft=font(13)
    d.text((20,14),f"{args.clip} / {args.tag}   reference (blue) vs port (orange)   body frame: pelvis origin, root orientation",font=fb,fill=(230,230,235))
    d.text((20,40),"front (X/Y)",font=ft,fill=(150,150,160)); d.text((470,40),"side (Z/Y)",font=ft,fill=(150,150,160))
    d.text((920,40),"reference",font=ft,fill=(120,180,240)); d.text((1060,40),"port",font=ft,fill=(240,150,70))
    top=70; rowh=330
    def draw_pose(dr, ox, oy, sc, j, color, width, front):
        def P(n):
            x,y,z=j[n]
            u = x if front else z
            v = y
            return (ox+u*sc, oy-v*sc)
        for a,b in BONES:
            dr.line([P(a),P(b)],fill=color,width=width)
        for n in JOINTS:
            px,py=P(n); r=3
            dr.ellipse([px-r,py-r,px+r,py+r],fill=color)
    for k,(ph,t,r,p) in enumerate(picks):
        y0=top+k*rowh
        d.text((20,y0-18),f"phase {ph:.2f}  t={t:.2f}s",font=f,fill=(210,210,220))
        # body-frame positions
        pq=p[1]  # orient both bodies with the port's root rotation
        ref_j={n:qrot(qconj(pq),vsub(tuple(c*S for c in qrot(C,r[2][n][0])),tuple(c*S for c in qrot(C,r[2]["pelvis"][0])))) for n in JOINTS}
        port_j={n:qrot(qconj(pq),vsub(p[2][n][0],p[2]["pelvis"][0])) for n in JOINTS}
        sc=150.0/1.56  # 1.56 m body -> 150 px
        for front,ox in ((True,30),(False,480)):
            base_y=y0+rowh-45
            draw_pose(d,ox,base_y,sc,ref_j,(90,150,230),7,front)
            draw_pose(d,ox,base_y,sc,port_j,(245,150,70),3,front)
            # pelvis marker
            px,py=ox,base_y; d.line([px-8,py,px+8,py],fill=(120,120,130),width=1); d.line([px,py-8,px,py+8],fill=(120,120,130),width=1)
    if args.out is None:
        os.makedirs(OUT,exist_ok=True); args.out=os.path.join(OUT,f"{args.clip}_{args.tag}.png")
    os.makedirs(os.path.dirname(os.path.abspath(args.out)),exist_ok=True)
    img.save(args.out,optimize=True)
    print("wrote",args.out,os.path.getsize(args.out))

if __name__=="__main__": main()
