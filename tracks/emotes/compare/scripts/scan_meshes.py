import os, sys
import UnityPy
D="/mnt/c/Program Files (x86)/Steam/steamapps/common/Lethal Company/Lethal Company_Data"
for fn in sorted(os.listdir(D)):
    if not fn.endswith(".assets"): continue
    p=os.path.join(D,fn)
    try: env=UnityPy.load(p)
    except Exception as e: print(fn,"ERR",e); continue
    hits=[]
    for o in env.objects:
        if o.type.name!="Mesh": continue
        try: tt=o.read_typetree()
        except Exception: continue
        bp=tt.get("m_BindPose") or []
        if 25 <= len(bp) <= 50:
            hits.append((tt.get("m_Name"),len(bp)))
    if hits:
        print(fn, hits[:12], "..." if len(hits)>12 else "")
