#!/usr/bin/env python3
"""Animated reference-vs-port skeleton MP4 (one 2 s segment per clip)."""
import csv, json, math, os, re, subprocess, sys
from PIL import Image, ImageDraw, ImageFont
sys.path.insert(0,os.path.dirname(os.path.abspath(__file__)))
import render_contact as RC
HERE=os.path.dirname(os.path.abspath(__file__)); TRACK=os.path.normpath(os.path.join(HERE,"..",".."))
REF=os.path.join(TRACK,"compare","ref"); PORT=os.path.join(TRACK,"compare","port")
OUTDIR="/tmp/emote_video"; OUT=os.path.join(TRACK,"compare","emote_compare.mp4")
CLIPS=["Clap","D_Clap","D_Middle_Finger","D_Sign","Idle1Fix","Middle_Finger","Prisyadka","Salute","Shy","Sign","The_Griddy","Twerk"]
def main():
    C=RC.parse_basis("/mnt/c/AlterumT2/BepInEx/LogOutput.log")
    meta=json.load(open(os.path.join(REF,"ref_meta.json"))); refLeg=meta["source_rest"]["leg_sum_l"]
    os.makedirs(OUTDIR,exist_ok=True)
    for f in os.listdir(OUTDIR): os.remove(os.path.join(OUTDIR,f))
    fb=RC.fnt(22); f=RC.fnt(15)
    n=0
    for clip in CLIPS:
        ref=RC.load(os.path.join(REF,clip+".csv")); port=RC.load(os.path.join(PORT,f"{clip}_fixed.csv"))
        j0=port[0][2]; S=(math.dist(j0["thigh_l"][0],j0["calf_l"][0])+math.dist(j0["calf_l"][0],j0["foot_l"][0]))/refLeg
        pf=[r for r in port if r[3]>=0.99] or port
        dur=ref[-1][0]
        for k in range(24):
            t=dur*k/24.0
            pr=min(pf,key=lambda r:abs(r[0]-t)); ri=min(range(len(ref)),key=lambda i:abs(ref[i][0]-t)); rr=ref[ri]
            rq=pr[1]
            refj={jj:RC.qrot(RC.qconj(rq),RC.vsub(tuple(c*S for c in RC.qrot(C,rr[2][jj][0])),tuple(c*S for c in RC.qrot(C,rr[2]["pelvis"][0])))) for jj in RC.JOINTS}
            portj={jj:RC.qrot(RC.qconj(rq),RC.vsub(pr[2][jj][0],pr[2]["pelvis"][0])) for jj in RC.JOINTS}
            img=Image.new("RGB",(1000,460),(18,18,22)); d=ImageDraw.Draw(img)
            d.text((16,10),f"{clip}   reference (blue) vs port fixed (orange)",font=fb,fill=(235,235,240))
            d.text((16,38),f"t={t:.2f}s / {dur:.2f}s   body frame: pelvis origin, root orientation",font=f,fill=(150,150,160))
            for front,ox in ((True,40),(False,520)):
                RC.draw_pose(d,ox+220,410,120.0,refj,(80,140,220),6,front)
                RC.draw_pose(d,ox+220,410,120.0,portj,(248,150,60),3,front)
                d.text((ox+200,60),"front" if front else "side",font=f,fill=(160,160,170))
            img.save(os.path.join(OUTDIR,f"f{n:04d}.png")); n+=1
    print("frames",n)
    subprocess.run(["ffmpeg","-y","-loglevel","error","-framerate","12","-i",os.path.join(OUTDIR,"f%04d.png"),
                    "-c:v","libx264","-pix_fmt","yuv420p","-crf","26",OUT],check=True)
    print("wrote",OUT,os.path.getsize(OUT))
if __name__=="__main__": main()
