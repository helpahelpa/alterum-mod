import UnityPy, sys, collections
for path in sys.argv[1:]:
    env = UnityPy.load(path)
    print("="*70); print(path, "unity:", getattr(env,"unity_version","?"))
    c = collections.Counter()
    names = collections.defaultdict(list)
    for obj in env.objects:
        n = obj.type.name
        c[n]+=1
        try:
            tt = obj.read_typetree()
            nm = tt.get("m_Name")
            if nm: names[n].append(nm)
        except Exception: pass
    for k,v in c.most_common(): print(f"  {k}: {v}")
    for k in ("Avatar","AnimatorController","GameObject","Mesh","AnimationClip","RuntimeAnimatorController"):
        if k in names:
            print(f"  -- {k} names --")
            for nm in sorted(set(names[k]))[:80]: print("     ", nm)
