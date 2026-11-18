using System;
using AlterumModelSwap.Contracts;

namespace AlterumModelSwap.Hot;

// Entry point the host looks up by name. Must have a parameterless constructor and must not
// touch Unity in it - Initialize() is where that starts.
public class HotModule : IHotModule
{
    private ModelSwapRuntime _runtime;
    private IHostContext _host;

    public void Initialize(IHostContext host, bool firstActivation)
    {
        _host = host;
        _runtime = new ModelSwapRuntime(host, firstActivation);
        host.LogInfo($"[hot] module initialized (firstActivation={firstActivation})");
    }

    public void Update(float unscaledDeltaTime)
    {
        _runtime?.Update(unscaledDeltaTime);
        if (_host == null) return;
        try { ClipRecorder.Tick(_host, unscaledDeltaTime); }
        catch (Exception e) { _host.LogError($"[clip] tick threw: {e.Message}"); }
    }
    public void LateUpdate() => _runtime?.LateUpdate();
    public void OnGUI() => _runtime?.OnGUI();

    public void Dispose()
    {
        // A recording owns native resources (RenderTexture, camera GameObject) that the next
        // generation cannot see, so the retiring one has to destroy them itself.
        try { ClipRecorder.Abort(_host); } catch { }
        try { _runtime?.Dispose(); } catch { }
        _runtime = null;
    }
}
