#!/usr/bin/env python3
"""Per-clip contact sheet: reference/port skeleton overlay + actual baseline/fixed RT frames."""
import argparse, csv, glob, json, math, os, re, sys
from PIL import Image, ImageDraw, ImageFont

HERE=os.path.dirname(os.path.abspath(__file__)); TRACK=os.path.normpath(os.path.join(HERE,"..",".."))
REF=os.path.join(TRACK,"compare","ref"); PORT=os.path.join(TRACK,"compare","port")
SHOTS="/mnt/c/AlterumT2/BepInEx/screens/t2"; OUT=os.path.join(TRACK,"compare","sheets")
JOINTS=["pelvis","spine_01","spine_02","spine_03","neck_01","head","clavicle_l","upperarm_l","lowerarm_l","hand_l","clavicle_r","upperarm_r","lowerarm_r","hand_r","thigh_l","calf_l","foot_l","ball_l","thigh_r","calf_r","foot_r","ball_r"]
BONES=[("pelvis","spine_01"),("spine_01","spine_02"),("spine_02","spine_03"),("spine_03","neck_01"),("neck_01","head"),
       ("spine_03","clavicle_l"),("clavicle_l","upperarm_l"),("upperarm_l","lowerarm_l"),("lowerarm_l","hand_l"),
       ("spine_03","clavicle_r"),("clavicle_r","upperarm_r"),("upperarm_r","lowerarm_r"),("lowerarm_r","hand_r"),
       ("pelvis","thigh_l"),("thigh_l","calf_l"),("calf_l","foot_l"),("foot_l","ball_l"),
       ("pelvis","thigh_r"),("thigh_r","calf_r"),("calf_r","foot_r"),("foot_r","ball_r")]
def qrot(q,v):
    x,y,z,w=q; vx,vy,vz=v
    tx=2*(y*vz-z*vy); ty=2*(z*vx-x*vz); tz=2*(x*vy-y*vx)
    return (vx+w*tx+(y*tz-z*ty), vy+w*ty+(z*tx-x*tz), vz+w*tz+(x*ty-y*tx))
def qconj(q): return (-q[0],-q[1],-q[2],q[3])
def vsub(a,b): return tuple(x-y for x,y in zip(a,b))
def load(path):
    rows=[]
    with open(path) as f:
        for row in csv.DictReader(f):
            t=float(row["t"]); fade=float(row.get("fade",1.0) or 1.0)
            rq=(float(row["root_qx"]),float(row["root_qy"]),float(row["root_qz"]),float(row["root_qw"]))
            j={n:((float(row[n+"_px"]),float(row[n+"_py"]),float(row[n+"_pz"])),
                  (float(row[n+"_qx"]),float(row[n+"_qy"]),float(row[n+"_qz"]),float(row[n+"_qw"]))) for n in JOINTS}
            rows.append((t,rq,j,fade))
    return rows
def parse_basis(log):
    txt=open(log,errors="replace").read()
    m=re.findall(r"\[emote:rig\] basis quat=\(([-0-9.]+),([-0-9.]+),([-0-9.]+),([-0-9.]+)\)",txt)
    return tuple(float(x) for x in m[-1]) if m else (0,0,0,1)
def fnt(sz):
    for p in ("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf","/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf"):
        if os.path.exists(p): return ImageFont.truetype(p,sz)
    return ImageFont.load_default()
def shot_runs(clip):
    runs={}
    for p in glob.glob(os.path.join(SHOTS,f"emote_{clip}_r*_f*_*.png")):
        m=re.search(rf"emote_{re.escape(clip)}_r(\d+)_f(\d+)_([0-9.]+)\.png$",os.path.basename(p))
        if not m: continue
        r=int(m.group(1)); runs.setdefault(r,[]).append((int(m.group(2)),float(m.group(3)),p))
    for r in runs: runs[r].sort()
    return runs
def draw_pose(dr,ox,oy,sc,j,color,width,front):
    def P(n):
        x,y,z=j[n]; u=x if front else z
        return (ox+u*sc, oy-y*sc)
    for a,b in BONES: dr.line([P(a),P(b)],fill=color,width=width)
    for n in JOINTS:
        px,py=P(n); dr.ellipse([px-3,py-3,px+3,py+3],fill=color)
def main():
    ap=argparse.ArgumentParser(); ap.add_argument("--clips",default="Clap,D_Clap,D_Middle_Finger,D_Sign,Idle1Fix,Middle_Finger,Prisyadka,Salute,Shy,Sign,The_Griddy,Twerk")
    ap.add_argument("--log",default="/mnt/c/AlterumT2/BepInEx/LogOutput.log"); ap.add_argument("--scale",type=float,default=0.95)
    args=ap.parse_args(); C=parse_basis(args.log)
    meta=json.load(open(os.path.join(REF,"ref_meta.json"))); refLeg=meta["source_rest"]["leg_sum_l"]
    errs=json.load(open(os.path.join(TRACK,"compare","errors_fixed.json")))
    os.makedirs(OUT,exist_ok=True)
    for clip in args.clips.split(","):
        ref=load(os.path.join(REF,clip+".csv")); port=load(os.path.join(PORT,f"{clip}_fixed.csv"))
        # scale from port bind-ish first frame
        j0=port[0][2]; portLeg=math.dist(j0["thigh_l"][0],j0["calf_l"][0])+math.dist(j0["calf_l"][0],j0["foot_l"][0])
        S=portLeg/refLeg
        runs=shot_runs(clip)
        if not runs: print("no shots",clip); continue
        order=sorted(runs)
        # Clap has extra single-fix runs after the paired set; keep the paired fixed/baseline runs.
        if clip=="Clap": order=[r for r in order if r not in (17,18,19,20)] or order
        fixed_r=order[-1]; base_r=order[-2] if len(order)>1 else order[-1]
        fr=sorted(runs[fixed_r]); br=sorted(runs[base_r])
        dur=ref[-1][0]
        n=min(len(fr),len(br),5)
        W=1500; rowh=270; H=70+n*rowh+10
        img=Image.new("RGB",(W,H),(18,18,22)); d=ImageDraw.Draw(img)
        fb=fnt(20); f=fnt(14); ft=fnt(12)
        d.text((16,10),f"{clip}   reference (blue) vs port fixed (orange)   |   actual port runs (left / right)",font=fb,fill=(235,235,240))
        d.text((16,38),"body frame: pelvis origin, model-root orientation; front + side; shared scale",font=ft,fill=(150,150,160))
        # legend
        d.rectangle([16,50,26,60],fill=(90,150,230)); d.text((30,48),"reference",font=ft,fill=(150,190,240))
        d.rectangle([100,50,110,60],fill=(245,150,70)); d.text((114,48),"port",font=ft,fill=(245,180,110))
        for i in range(n):
            fi,frac,fp=fr[i]; bi,bf, bp=br[i]
            row=70+i*rowh
            t=frac*dur
            # nearest post-fade port / ref
            pf=[r for r in port if r[3]>=0.99] or port
            pr=min(pf,key=lambda r:abs(r[0]-t))
            ri=min(range(len(ref)),key=lambda k:abs(ref[k][0]-t)); rr=ref[ri]
            rq=pr[1]
            refj={jj:qrot(qconj(rq),vsub(tuple(c*S for c in qrot(C,rr[2][jj][0])),tuple(c*S for c in qrot(C,rr[2]["pelvis"][0])))) for jj in JOINTS}
            portj={jj:qrot(qconj(rq),vsub(pr[2][jj][0],pr[2]["pelvis"][0])) for jj in JOINTS}
            sc=95.0*args.scale  # px/m
            for front,ox in ((True,20),(False,300)):
                draw_pose(d,ox+150,row+230,sc,refj,(80,140,220),6,front)
                draw_pose(d,ox+150,row+230,sc,portj,(248,150,60),3,front)
                d.text((ox+120,row+8),"front" if front else "side",font=ft,fill=(160,160,170))
            d.text((20,row+8),f"phase {frac:.2f}  t={t:.2f}s",font=f,fill=(220,220,230))
            try:
                im=Image.open(fp).convert("RGB").resize((250,250))
                img.paste(im,(620,row+8))
            except Exception: pass
            try:
                im=Image.open(bp).convert("RGB").resize((250,250))
                img.paste(im,(880,row+8))
            except Exception: pass
            d.text((620,row+258),f"actual run r{fixed_r}",font=ft,fill=(200,200,210))
            d.text((880,row+258),f"actual run r{base_r}",font=ft,fill=(200,200,210))
        # error summary
        st=errs.get(clip,{})
        js=st.get("joints",{})
        worst=sorted(js,key=lambda k:-js[k]["pos_mean"])[:4]
        txt="worst pos: "+", ".join(f"{k} {js[k]['pos_mean']*100:.0f}cm" for k in worst)
        d.text((1160,50),txt,font=ft,fill=(220,180,150))
        if st.get("pelvis"):
            d.text((1160,68),f"pelvis range err {max(st['pelvis']['range_err'])*100:.1f}cm",font=ft,fill=(160,200,160))
        outp=os.path.join(OUT,f"{clip}_compare.png")
        img.save(outp,optimize=True)
        sz=os.path.getsize(outp)
        if sz>590*1024:
            img2=img.quantize(colors=128).convert("RGB"); img2.save(outp,optimize=True); sz=os.path.getsize(outp)
        print(clip,outp,sz,"bytes",f"runs fixed={fixed_r} base={base_r}")
if __name__=="__main__": main()
