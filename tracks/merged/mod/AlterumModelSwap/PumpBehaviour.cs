using System;
using UnityEngine;

namespace AlterumModelSwap;

// The only il2cpp-injected type. It never contains mod logic - it just forwards into the
// reloadable assembly, because an injected type's method pointers are bound once at
// registration and can never be replaced.
public class PumpBehaviour : MonoBehaviour
{
    public PumpBehaviour(IntPtr ptr) : base(ptr) { }

    private void Update()
    {
        // Permanent identity guard first: it must run even when the hot module is absent, mid
        // reload, or failed to initialise. It catches its own exceptions internally.
        try { InstanceLabelGuard.TickEarly(); } catch { }

        // The net bridge is host-owned and independent of the hot module: it must keep its
        // registration alive across module loads, and run even with no module at all.
        try { NetBridge.Instance.Tick(Time.unscaledDeltaTime); } catch { }
        try { HotLoader.Tick(Time.unscaledDeltaTime); } catch { }
    }
    private void LateUpdate()
    {
        // A display-mode change can replace the Unity window after Update; check once more before
        // the frame is presented.
        try { InstanceLabelGuard.TickLate(); } catch { }
        try { HotLoader.Late(); } catch { }
    }

    // State bookkeeping only: the canonical title is deliberately left in place on shutdown.
    private void OnApplicationQuit() { try { InstanceLabelGuard.MarkQuitting(); } catch { } }
    private void OnGUI() { try { HotLoader.Gui(); } catch { } }
}
