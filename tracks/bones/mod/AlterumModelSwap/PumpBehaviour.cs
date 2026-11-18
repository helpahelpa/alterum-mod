using System;
using UnityEngine;

namespace AlterumModelSwap;

// The only il2cpp-injected type. It never contains mod logic - it just forwards into the
// reloadable assembly, because an injected type's method pointers are bound once at
// registration and can never be replaced.
public class PumpBehaviour : MonoBehaviour
{
    public PumpBehaviour(IntPtr ptr) : base(ptr) { }

    private void Update() { try { HotLoader.Tick(Time.unscaledDeltaTime); } catch { } }
    private void LateUpdate() { try { HotLoader.Late(); } catch { } }
    private void OnGUI() { try { HotLoader.Gui(); } catch { } }
}
