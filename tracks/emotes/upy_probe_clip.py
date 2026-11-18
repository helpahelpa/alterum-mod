import UnityPy, json, sys
env = UnityPy.load("ref/unz/BepInEx/plugins/BetterEmotes/animationsbundle")
for obj in env.objects:
    if obj.type.name!="AnimationClip": continue
    tt=obj.read_typetree()
    if tt.get("m_Name")!="Clap": continue
    print("keys:", list(tt.keys()))
    mc = tt.get("m_MuscleClip")
    print("\nMuscleClipWrapper type:", type(mc))
    print(json.dumps({k:(v if not isinstance(v,(list,dict)) else f"<{type(v).__name__} len={len(v)}>") for k,v in tt.items()}, indent=1)[:2500])
    print("\n--- m_MuscleClip ---")
    print(json.dumps({k:(v if not isinstance(v,(list,dict)) else f"<{type(v).__name__} len={len(v)}>") for k,v in mc.items()}, indent=1)[:2000])
    mclip = mc.get("m_Clip")
    print("\n--- m_Clip ---")
    for k,v in mclip.items():
        if isinstance(v,(list,dict)): print(f"  {k}: <{type(v).__name__} len={len(v)}>")
        else: print(f"  {k}: {v}")
    print("\n--- binding constant ---")
    bc = mclip.get("m_ClipBindingConstant")
    print("keys:", list(bc.keys()))
    gb = bc.get("genericBindings")
    print("genericBindings:", len(gb))
    for b in gb[:80]: print("   ", b)
    print("\n--- streams ---")
    for k in ("m_StreamedClip","m_DenseClip","m_ConstantClip"):
        s = mclip.get(k)
        print(f"  {k}: keys={list(s.keys()) if isinstance(s,dict) else type(s)}")
        if isinstance(s,dict):
            for kk,vv in s.items():
                if isinstance(vv,list): print(f"     {kk}: <list len={len(vv)}> first={vv[:8]}")
                else: print(f"     {kk}: {vv}")
    break
