import sys, os
import UnityPy
D="/mnt/c/Program Files (x86)/Steam/steamapps/common/Lethal Company/Lethal Company_Data"
p=sys.argv[1] if os.path.exists(sys.argv[1]) else os.path.join(D,sys.argv[1])
env=UnityPy.load(p)
scripts={}
for o in env.objects:
    if o.type.name=="MonoScript":
        tt=o.read_typetree(); scripts[o.path_id]=tt
print("scripts",len(scripts))
for pid,tt in scripts.items():
    cn=tt.get("m_ClassName","")
    if "Chain" in cn or "IK" in cn:
        print(pid, tt.get("m_Namespace"), cn, tt.get("m_AssemblyName"))
# which MonoBehaviours use them
for o in env.objects:
    if o.type.name=="MonoBehaviour":
        tt=o.read_typetree()
        sp=tt.get("m_Script",{}).get("m_PathID",0)
        s=scripts.get(sp)
        if s and ("Chain" in s.get("m_ClassName","") or "IK" in s.get("m_ClassName","")):
            print("INSTANCE",o.path_id,s.get("m_ClassName"),"GO",tt.get("m_GameObject",{}).get("m_PathID"))
            for k,v in tt.items():
                if k in ("m_GameObject","m_Script","m_Enabled","m_EditorHideFlags","m_EditorClassIdentifier"): continue
                print("   ",k,"=",v if not isinstance(v,dict) or "m_PathID" not in v else "PPtr(%d)"%v["m_PathID"])
