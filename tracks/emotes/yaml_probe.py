import re, sys, collections, UnityPy

def parse_anim(path):
    txt=open(path,encoding='utf-8',errors='replace').read()
    name=re.search(r'm_Name: (\S+)', txt)
    out={"name":name.group(1) if name else "?", "curves":collections.defaultdict(dict), "paths":set()}
    # split into curve blocks by top-level list entries
    for key in ("m_EulerCurves","m_PositionCurves","m_ScaleCurves","m_FloatCurves","m_RotationCurves","m_CompressedRotationCurves"):
        m=re.search(r'  '+key+r':(.*?)(?=\n  m_[A-Za-z]+:|\Z)', txt, re.S)
        if not m: continue
        block=m.group(1)
        for cm in re.finditer(r'- curve:\n(.*?)(?=\n  - curve:|\Z)', block, re.S):
            body=cm.group(1)
            p=re.search(r'\n    path: (.*)', body)
            attr=re.search(r'\n    attribute: (.*)', body)
            cls=re.search(r'\n    classID: (.*)', body)
            times=re.findall(r'\n        time: (\S+)\n        value: (\{[^}]*\}|-?[0-9.eE+-]+)', body)
            out["paths"].add(p.group(1).strip() if p else "?")
            if p: out["curves"][(key,p.group(1).strip(), attr.group(1).strip() if attr else None)] = times[:3]
    return out, txt

for f in ("Clap","Middle_Finger","Sign"):
    o,txt=parse_anim(f"src/lc-better-emotes-main/Unity/moreemotes/{f}.anim")
    print("="*60, o["name"], "path-entries:", len(o["paths"]))
    print("  sample:", list(o["paths"])[:5])
    print("  curve groups:", collections.Counter(k[0] for k in o["curves"]))
    for k,v in list(o["curves"].items())[:4]: print("   ", k, "keys:", len(v), v[:2])

# bundle hash set for Clap
env=UnityPy.load("ref/unz/BepInEx/plugins/BetterEmotes/animationsbundle")
for obj in env.objects:
    if obj.type.name!="AnimationClip": continue
    tt=obj.read_typetree()
    if tt.get("m_Name")!="Clap": continue
    bh={str(b['path']) for b in tt["m_ClipBindingConstant"]["genericBindings"]}
    o,_=parse_anim("src/lc-better-emotes-main/Unity/moreemotes/Clap.anim")
    yh={p for p in o["paths"] if p.isdigit() or (p.startswith('-') and p[1:].isdigit())}
    yh={str(int(p) & 0xffffffff) for p in yh}
    print("\nBundle unique paths:", len(bh), "YAML numeric paths:", len(yh))
    print("  in both:", len(bh & yh))
    print("  bundle only:", sorted(bh - yh)[:10])
    print("  yaml only:", sorted(yh - bh)[:10])
    break
