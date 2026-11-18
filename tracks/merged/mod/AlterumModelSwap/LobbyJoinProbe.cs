using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AlterumModelSwap;

// Test scaffolding for stage-1 networking, not part of the bridge.
//
// The shipped UI automation (`@JoinLobby=<code>` in a ClickSequence) fills the first *active*
// TMP_InputField, which in this menu layout is the Settings > Framerate Limit field, so the
// connect-by-code dialog cannot be driven unattended and two instances never meet in a lobby.
// This presses the same field and the same button, addressed by hierarchy path instead of by
// "first found", so the two-instance bridge test can be reproduced with one request line.
//
// Driven from `<ScreensDir>/net.request`:  join <CODE>
internal static class LobbyJoinProbe
{
    internal static bool Join(string code)
    {
        try
        {
            if (string.IsNullOrEmpty(code)) return false;

            var field = FindIn<TMP_InputField>("Connect via code");
            if (field == null)
            {
                HostPlugin.L?.LogWarning("[net:join] no active 'Connect via code' input field (is the code dialog open?)");
                return false;
            }

            HostPlugin.L?.LogInfo($"[net:join] field '{PathOf(field.transform)}' <- '{code}'");
            field.text = code;
            try { field.onValueChanged.Invoke(code); } catch { }
            try { field.onEndEdit.Invoke(code); } catch { }

            // Find the join button two ways: by full path (unambiguous) and, failing that, by a
            // scan over every active Button whose path mentions the dialog.
            Button button = null;
            var go = GameObject.Find("UI/Menu Canvas/Content/Connect via code/Gothic Yellow Button Variant");
            if (go != null) button = go.GetComponent(Il2CppType.Of<Button>())?.TryCast<Button>();
            if (button == null) button = FindIn<Button>("Connect via code");
            if (button == null)
            {
                int active = 0;
                var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Button>());
                if (all != null)
                    for (int i = 0; i < all.Length; i++)
                    {
                        var b = all[i].TryCast<Button>();
                        if (b != null && b.gameObject != null && b.gameObject.activeInHierarchy) active++;
                    }
                HostPlugin.L?.LogWarning($"[net:join] no 'Connect via code' join button (go={(go != null)} activeButtons={active} total={(all?.Length ?? -1)})");
                return false;
            }

            HostPlugin.L?.LogInfo($"[net:join] press '{PathOf(button.transform)}' interactable={button.interactable}");
            button.onClick.Invoke();
            return true;
        }
        catch (Exception e)
        {
            HostPlugin.L?.LogWarning($"[net:join] failed {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    private static T FindIn<T>(params string[] pathParts) where T : Component
    {
        var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>());
        if (all == null) return null;
        for (int i = 0; i < all.Length; i++)
        {
            var c = all[i].TryCast<T>();
            if (c == null) continue;
            var go = c.gameObject;
            if (go == null || !go.activeInHierarchy) continue;
            var path = PathOf(c.transform);
            var ok = true;
            foreach (var part in pathParts)
                if (path.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0) { ok = false; break; }
            if (ok) return c;
        }
        return null;
    }

    private static string PathOf(Transform t)
    {
        if (t == null) return "";
        var path = t.name;
        var p = t.parent;
        var guard = 0;
        while (p != null && guard++ < 24)
        {
            path = p.name + "/" + path;
            p = p.parent;
        }
        return path;
    }
}
