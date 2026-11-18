import sys, os, json
import UnityPy
D="/mnt/c/Program Files (x86)/Steam/steamapps/common/Lethal Company/Lethal Company_Data"
files=[f for f in os.listdir(D) if f.endswith('.assets') or f=='globalgamemanagers']
needles=("metarig","Metarig","Scavenger","PlayerModel","SK_Cowboy","Rig 1")
for f in sorted(files):
    p=os.path.join(D,f)
    try:
        env=UnityPy.load(p)
    except Exception as e:
        print(f, "ERR", e); continue
    hits={}
    counts={}
    for o in env.objects:
        counts[o.type.name]=counts.get(o.type.name,0)+1
        if o.type.name in ("GameObject","Transform","AnimationClip","Animator","SkinnedMeshRenderer","PrefabInstance"):
            try:
                tt=o.read_typetree()
            except Exception:
                continue
            n=tt.get("m_Name") or ""
            if any(x in n for x in needles):
                hits.setdefault(o.type.name,[]).append(n)
    print(f"== {f} objects={len(env.objects)} {sorted(counts.items(), key=lambda kv:-kv[1])[:6]}")
    for k,v in hits.items():
        print("   ",k,len(v),sorted(set(v))[:10])
