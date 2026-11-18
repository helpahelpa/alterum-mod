#!/usr/bin/env python3
"""Project a joint CSV into a capture frame and draw the skeleton, to check what is actually rendered."""
import csv, math, sys, os
from PIL import Image, ImageDraw

def P(r, j): return (float(r[j+'_px']), float(r[j+'_py']), float(r[j+'_pz']))
JOINTS = ["pelvis","spine_01","spine_02","spine_03","neck_01","head","clavicle_l","upperarm_l",
          "lowerarm_l","hand_l","clavicle_r","upperarm_r","lowerarm_r","hand_r","thigh_l","calf_l",
          "foot_l","ball_l","thigh_r","calf_r","foot_r","ball_r"]
BONES = [("pelvis","spine_01"),("spine_01","spine_02"),("spine_02","spine_03"),("spine_03","neck_01"),
         ("neck_01","head"),("spine_03","clavicle_l"),("clavicle_l","upperarm_l"),
         ("upperarm_l","lowerarm_l"),("lowerarm_l","hand_l"),("spine_03","clavicle_r"),
         ("clavicle_r","upperarm_r"),("upperarm_r","lowerarm_r"),("lowerarm_r","hand_r"),
         ("pelvis","thigh_l"),("thigh_l","calf_l"),("calf_l","foot_l"),("foot_l","ball_l"),
         ("pelvis","thigh_r"),("thigh_r","calf_r"),("calf_r","foot_r"),("foot_r","ball_r")]
LEFT = {"clavicle_l","upperarm_l","lowerarm_l","hand_l","thigh_l","calf_l","foot_l","ball_l"}
RIGHT = {"clavicle_r","upperarm_r","lowerarm_r","hand_r","thigh_r","calf_r","foot_r","ball_r"}
CENTER = {"pelvis","spine_01","spine_02","spine_03","neck_01","head"}

def sub(a,b): return tuple(x-y for x,y in zip(a,b))
def dot(a,b): return sum(x*y for x,y in zip(a,b))
def cross(a,b): return (a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0])
def norm(a):
    n=math.sqrt(sum(x*x for x in a)) or 1; return tuple(x/n for x in a)

class Cam:
    def __init__(self, eye, focus, fov, w, h, yaw_only=False):
        self.eye = eye; self.w = w; self.h = h
        f = (h/2) / math.tan(math.radians(fov)/2)
        z = norm(sub(focus, eye)); x = norm(cross((0,1,0), z)); y = cross(z, x)
        self.f, self.x, self.y, self.z = f, x, y, z
    def project(self, p):
        d = sub(p, self.eye)
        cx, cy, cz = dot(d,self.x), dot(d,self.y), dot(d,self.z)
        if cz <= 0.01: return None
        return (self.w/2 + self.f*cx/cz, self.h/2 - self.f*cy/cz)

def draw(img, rows, cam, color_of=lambda j:(0,255,0), width=2, r=3, label=False):
    d = ImageDraw.Draw(img)
    row = rows
    for a,b in BONES:
        pa, pb = cam.project(P(row,a)), cam.project(P(row,b))
        if pa and pb:
            d.line([pa,pb], fill=color_of(a), width=width)
    for j in JOINTS:
        p = cam.project(P(row,j))
        if p: d.ellipse([p[0]-r,p[1]-r,p[0]+r,p[1]+r], fill=color_of(j))
    if label:
        p = cam.project(P(row,"hand_l"))
        if p: d.text((p[0]+4,p[1]), "hand.L", fill=(255,255,0))
        p = cam.project(P(row,"head"))
        if p: d.text((p[0]+4,p[1]), "head", fill=(255,255,0))
