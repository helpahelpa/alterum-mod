using System;
using AlterumModelSwap.Hot;

// Only the Unity wrapper is fake; production selection-path implementation is linked unmodified.
namespace UnityEngine
{
    public class Transform { public string name; public Transform parent; }
}
class Probe
{
    static void Main()
    {
        const string path = "Lobby Global/User Interface/Canvases/Master Canvas/Pause Menu/Viewport/Game Menu Tabs/Menu Tabs/Exit to Menu Button Variant";
        UnityEngine.Transform t = null;
        foreach (var part in path.Split('/')) t = new UnityEngine.Transform { name = part, parent = t };
        if (path.Length <= 96 || UiPath.Full(t) != path) throw new Exception("full selection path was truncated");
        for (int i = 0; i < 20; i++) t = new UnityEngine.Transform { name = "nested" + i, parent = t };
        if (!UiPath.Full(t).StartsWith(path + "/")) throw new Exception("deep hierarchy lost its root");
        if (UiPath.Full(null) != "") throw new Exception("null path");
        t.parent = t;
        try { UiPath.Full(t); throw new Exception("cyclic hierarchy was accepted"); }
        catch (InvalidOperationException) { }
        Console.WriteLine("PASS real UiPath: full long path, deep path, null and cycle refusal");
    }
}
