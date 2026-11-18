import UnityPy, sys, collections
path = sys.argv[1]
env = UnityPy.load(path)
clips = []
for obj in env.objects:
    if obj.type.name != "AnimationClip": continue
    try: d = obj.read()
    except Exception as e:
        print("ERR read clip", e); continue
    name = getattr(d, "m_Name", "?")
    clips.append((name, obj.path_id, d))
print("clip count:", len(clips))
for name, pid, d in sorted(clips, key=lambda x: x[0]):
    m_Euler = getattr(d, "m_EulerCurves", []) or []
    m_Pos = getattr(d, "m_PositionCurves", []) or []
    m_Scl = getattr(d, "m_ScaleCurves", []) or []
    m_Flt = getattr(d, "m_FloatCurves", []) or []
    paths = set()
    for arr in (m_Euler, m_Pos, m_Scl):
        for c in arr:
            paths.add(c.path if isinstance(c.path, str) else str(c.path))
    sample = sorted(paths)[:8]
    length = None
    try:
        length = d.m_AnimationClipSettings.m_StopTime
    except Exception: pass
    print(f"{name:24s} rot/euler={len(m_Euler):4d} pos={len(m_Pos):3d} scale={len(m_Scl):3d} float={len(m_Flt):3d} uniqpaths={len(paths):3d} len={length}")
    if name in ("The_Griddy","Clap","Middle_Finger","Sign","Twerk","Idle1","Walk"):
        for p in sorted(paths)[:10]: print("     ", p)
