using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using AlterumModelSwap.Contracts;
using Gameplay.PlayerSystem;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// The Reel of Fortune as a genuine interactable: the game's own looker finds it, the game's own
// world-space prompt ("E" + the localized action name) appears on it, and the game's own accepted
// interaction runs the spin. Nothing here draws a prompt.
//
// Chain (traced from the IL2CPP metadata of InteractionSystem):
//   DistantInteractableLooker  (local player) overlap-spheres on layer 'InteractableLookable',
//                              picks the best LookableDistantInteractable by _lookableCenter dot
//   LookableDistantInteractable  _collider (can-see raycast), _lookableCenter, _ui
//   LookableInteractable<T>      _interactable (Interactable), _interactionProvider (TapInteraction),
//                                _lookableConstraints / _constraintPostExcluders
//   Interactable (NetworkBehaviour) CanInteract -> InteractLocal -> InteractedLocally
//   DistantInteractionUIPresenter Instance  owns the pooled world-space view; each lookable's
//                                DistantInteractionWorldSpaceUI (owner) asks it to show/hide
//   InteractableInputHandler    binds the Interaction's key (E) while a lookable is active and
//                                performs the interaction
//
// `interact.request` drives it unattended (see Poll). Everything here swallows its own exceptions.
internal sealed class ReelInteraction
{
    internal const string RequestFile = "interact.request";
    private static float _nextPoll;

    private bool _built;
    private string _state = "not built";
    private int _accepted;

    internal string State => _state;
    internal int AcceptedCount => _accepted;

    // ---- frame hook (called from LobbyPropRuntime.Tick) --------------------------------
    // One instance per booth: two saved anchors are two booths, each with its own collider, look
    // centre and InteractedLocally subscription. Nothing here is shared between booths except the
    // request-poll cadence and the log sink.
    internal void Tick(IHostContext log, LobbyPropRuntime rt, float dt)
    {
        try
        {
            if (rt == null || rt.Root == null) return;
            _log = log; _rt = rt;
            EnsureBuilt(log, rt);
            PressTick(log);
            FrameTick(log);
            ProviderGuard(log);
            if (rt.RequestOwner) Poll(log, rt);
        }
        catch (Exception e) { log?.LogWarning($"[reel:act] tick failed: {e.Message}"); }
    }

    private void Poll(IHostContext log, LobbyPropRuntime rt)
    {
        if (log == null) return;
        if (Time.unscaledTime < _nextPoll) return;
        _nextPoll = Time.unscaledTime + 0.35f;
        string dir = log.ScreensDir;
        if (string.IsNullOrEmpty(dir)) return;
        string path = System.IO.Path.Combine(dir, RequestFile);
        if (!File.Exists(path)) return;
        string text;
        try { text = File.ReadAllText(path); } catch { return; }
        try { File.Delete(path); } catch { }
        log.LogInfo($"[reel:act] request {text.Replace("\r", " ").Replace("\n", " | ")}");
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            // A trailing '@<n>' addresses booth n (1-based); the request file is single, the booths
            // are not. Without it every line acts on the booth that owns the poll (the first one).
            int at = line.LastIndexOf('@');
            if (at > 0)
            {
                int target;
                if (int.TryParse(line.Substring(at + 1), out target))
                {
                    var other = At(target);
                    line = line.Substring(0, at).Trim();
                    if (other == null) { log.LogWarning($"[reel:act] request '{line}@{target}': no booth {target} (live={Live.Count})"); continue; }
                    if (!ReferenceEquals(other, this)) { other.HandleRequestLine(log, line); continue; }
                }
            }
            if (string.Equals(line, "booths", StringComparison.OrdinalIgnoreCase)) { DumpBooths(log); continue; }
            if (line.Equals("clones", StringComparison.OrdinalIgnoreCase)) { CensusClones(log); continue; }
            if (line.Equals("probe", StringComparison.OrdinalIgnoreCase)) Probe(log, rt);
            else if (line.Equals("build", StringComparison.OrdinalIgnoreCase)) Build(log, rt);
            else if (line.Equals("clear", StringComparison.OrdinalIgnoreCase)) Teardown(log);
            else if (line.Equals("state", StringComparison.OrdinalIgnoreCase)) DumpState(log, rt);
            else if (line.StartsWith("press=", StringComparison.OrdinalIgnoreCase)) Press(log, line.Substring(6).Trim());
            else if (line.StartsWith("standat=", StringComparison.OrdinalIgnoreCase)) StandPlayerAt(log, line.Substring(8).Trim());
            else if (line.StartsWith("stand=", StringComparison.OrdinalIgnoreCase)) StandPlayer(log, line.Substring(6).Trim());
            else if (line.StartsWith("shot=", StringComparison.OrdinalIgnoreCase)) ScreenShot.Capture(log, line.Substring(5).Trim());
            else if (line.Equals("state", StringComparison.OrdinalIgnoreCase))
                log.LogInfo($"[reel:act] state built={_built} accepted={_accepted} '{_state}'");
            else if (line.Equals("hierarchy", StringComparison.OrdinalIgnoreCase)) Hierarchy(log, rt);
            else if (line.Equals("why", StringComparison.OrdinalIgnoreCase)) Why(log);
            else if (line.Equals("showui", StringComparison.OrdinalIgnoreCase)) ForceUi(log, true);
            else if (line.Equals("hideui", StringComparison.OrdinalIgnoreCase)) ForceUi(log, false);
            else if (line.Equals("providers", StringComparison.OrdinalIgnoreCase)) Providers(log);
            else if (line.Equals("handlerfire", StringComparison.OrdinalIgnoreCase)) HandlerFire(log);
            else if (line.Equals("startlook", StringComparison.OrdinalIgnoreCase)) LookHandoff(log, true);
            else if (line.Equals("stoplook", StringComparison.OrdinalIgnoreCase)) LookHandoff(log, false);
            else if (line.StartsWith("frames=", StringComparison.OrdinalIgnoreCase)) StartFrameLog(log, line.Substring(7).Trim());
            else if (line.Equals("logs", StringComparison.OrdinalIgnoreCase)) SetLookerLogs(log, true);
            else if (line.Equals("nologs", StringComparison.OrdinalIgnoreCase)) SetLookerLogs(log, false);
            else log.LogWarning($"[reel:act] unknown request line '{line}'");
        }
    }

    // ---- build / teardown ---------------------------------------------------------------

    private GameObject _go;
    private float _nextBuildAttempt;                // backoff so a failing build cannot retry every frame
    private string _retryState = "";                // the failure we already announced a retry for
    private const float BuildRetrySeconds = 2f;
    private InteractionSystem.Implementation.Lookables.LookableDistantInteractable _lookable;
    private InteractionSystem.Implementation.UI.DistantInteractionWorldSpaceUI _ui;
    private InteractionSystem.Core.Interactable _interactable;
    private Unity.Netcode.NetworkObject _netObj;
    private Component _prompt;                       // the WS-UI prompt component (localised label)
    private bool _promptLabelOk;                     // its action label resolves to real text
    private string _promptLabelSource = "none";
    private Il2CppSystem.Action<InteractionSystem.Core.Interactor> _interacted;
    private bool _subscribed;
    private string _source = "none";
    private string _lastPrompt = "";

    internal bool Built => _built;

    // True only when the game's own world-space prompt is the thing the player will see: the
    // cloned interactable exists, carries its DistantInteractionWorldSpaceUI and its localised
    // action label resolves. LobbyPropRuntime uses this to decide between the native prompt and the
    // drawn camera quad - and to never show both.
    internal bool NativePromptLive
    {
        get
        {
            try { return _built && _ui != null && _prompt != null && _promptLabelOk; }
            catch { return false; }
        }
    }

    internal string PromptLabelSource => _promptLabelSource;

    // Reuse the game's own minimal interactable prefab: layer 'InteractableLookable', a trigger
    // BoxCollider (the looker's can-see raycast target), LookableDistantInteractable, EmptyInteractable
    // and the 'Interaction WS UI' child carrying the world-space prompt. Nothing is authored here
    // beyond the pose, the collider box and the reference to the player's tap-interaction provider.
    internal bool Build(IHostContext log, LobbyPropRuntime rt)
    {
        if (_built) { log?.LogInfo("[reel:act] build: already built"); return true; }
        if (rt?.Root == null) { log?.LogWarning("[reel:act] build: no prop root"); return false; }
        GameObject holder = null, go = null;
        try
        {
            var src = FindInteractablePrefab(log, out string how);
            if (src == null) { _state = "build failed: no interactable prefab"; log?.LogWarning($"[reel:act] {_state}"); return false; }
            _source = how;
            // Inactive holder: the clone's Awake/Start do not run yet, so the template's deliberate
            // AlwaysFalseConstraint can be removed before the constraint collection is collected.
            holder = new GameObject("ReelInteractableHolder");
            holder.SetActive(false);
            // Non-generic Instantiate: the GameObject argument is cast to UnityEngine.Object, so overload
            // resolution cannot select Instantiate<T>(T). The `as GameObject` that used to sit on the
            // result never affected that choice. The generic binds an IL2CPP generic interop call which
            // traps natively in this build; a managed catch cannot survive an access violation.
            go = UnityEngine.Object.Instantiate((UnityEngine.Object)src, holder.transform)?.TryCast<GameObject>();
            if (go == null) { _state = "build failed: instantiate returned null"; log?.LogWarning($"[reel:act] {_state}"); return false; }
            go.name = "Reel of Fortune Interactable";
            StripAlwaysFalse(log, go);
            RewireLookableConstraints(log, go);
            // Hand the clone the game-constructed provider before it awakes, and drop the clone's
            // own provider: it was never Construct()ed, so it cannot resolve the player's
            // interaction and calling InitializeInteraction() on it is a null dereference.
            PrepareProvider(log, go);
            go.transform.SetParent(rt.Root.transform, false);
            go.SetActive(true);
            // The prop face is +Z at z ~ 0.185; the sign centre is 1.96 above the base. The root sits
            // at the face, the collider covers the cabinet, the WS-UI child holds the prompt anchor.
            go.transform.localPosition = new Vector3(0f, 1.960f, 0.260f);
            go.transform.localRotation = Quaternion.identity;
            _go = go;

            var box = go.GetComponent<BoxCollider>();
            if (box != null)
            {
                box.isTrigger = true;
                box.center = new Vector3(0f, 0f, -0.05f);
                box.size = new Vector3(1.00f, 1.60f, 0.34f);
                log?.LogInfo($"[reel:act] build collider '{PathOf(box.transform)}' layer={go.layer} " +
                             $"trigger={box.isTrigger} center={V(box.center)} size={V(box.size)} " +
                             $"world={V(box.bounds.center)}/{V(box.bounds.size)}");
            }
            else log?.LogWarning("[reel:act] build: prefab has no BoxCollider");

            _lookable = go.GetComponent<InteractionSystem.Implementation.Lookables.LookableDistantInteractable>();
            _interactable = go.GetComponent<InteractionSystem.Core.Interactable>();
            _netObj = go.GetComponent<Unity.Netcode.NetworkObject>();
            if (_lookable == null) { _state = "build failed: no LookableDistantInteractable"; log?.LogWarning($"[reel:act] {_state}"); return false; }

            var centre = F(_lookable, "_lookableCenter") as Transform;
            if (centre == null)
            {
                centre = go.transform;
                SetF(_lookable, "_lookableCenter", centre);
            }
            centre.localPosition = new Vector3(0f, 0f, 0.10f);
            log?.LogInfo($"[reel:act] build look centre='{PathOf(centre)}' world={V(centre.position)}");
            // The template is an achievement totem and overrides the looker's facing dot to 0.925.
            // A plain interactable uses the looker's 0.9; inheriting the stricter override is how a
            // lookable ends up held as 'in radius' (Attraction) but never promoted to Active.
            log?.LogInfo($"[reel:act] build facingDot override={F(_lookable, "_overrideRequireFacingDot")} " +
                         $"value={F(_lookable, "_overridenRequiredFacingDot")}");
            SetF(_lookable, "_overrideRequireFacingDot", false);

            _ui = F(_lookable, "_ui") as InteractionSystem.Implementation.UI.DistantInteractionWorldSpaceUI;
            if (_ui == null) _ui = go.GetComponentInChildren<InteractionSystem.Implementation.UI.DistantInteractionWorldSpaceUI>(true);
            if (_ui != null)
            {
                SetF(_lookable, "_ui", _ui);
                var anchor = F(_ui, "_anchor") as Transform;
                if (anchor == null) { anchor = centre; SetF(_ui, "_anchor", anchor); }
                var prompt = F(_ui, "_prompt") as Component;
                if (prompt != null)
                {
                    SetF(prompt, "_lookableDistanceInteractable", _lookable);
                    // PromptBase.CanShow() gates the active view: a prompt whose Show flag is off
                    // is what the lookable reports as 'Attraction' instead of the E label.
                    try
                    {
                        log?.LogInfo($"[reel:act] build prompt Show(before)={F(prompt, "Show")} CanShow(before)={InvokeBool(prompt, "CanShow")}");
                        SetF(prompt, "Show", true);
                        try { var m0 = prompt.GetType().GetMethod("OnUpdated", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null); m0?.Invoke(prompt, null); } catch { }
                        log?.LogInfo($"[reel:act] build prompt Show(after)={F(prompt, "Show")} CanShow(after)={InvokeBool(prompt, "CanShow")}");
                    }
                    catch (Exception e) { log?.LogWarning($"[reel:act] build prompt Show: {e.Message}"); }
                    log?.LogInfo($"[reel:act] build prompt type={TypeName(prompt)} lookable='{((Component)F(prompt, "_lookableDistanceInteractable"))?.name}' " +
                                 $"action={Loc(F(prompt, "_currentActionName"))}");
                    _prompt = prompt;
                    InitPromptLabel(log, prompt);
                }
                else log?.LogWarning("[reel:act] build: WS UI has no prompt component");
            }
            else log?.LogWarning("[reel:act] build: no DistantInteractionWorldSpaceUI");

            // The lookable's InteractionProvider must resolve the local player's own TapInteraction
            // (key 'e'). A cloned prefab's provider is never Construct()ed with a player mediator, so
            // its _interaction stays null and the native handler has no key to bind: the looker can
            // hold the object as Active and E still does nothing. Resolve it from the live player.
            WireProvider(log);

            // Spawn: the prefab is already registered (it came out of the network prefab list), so
            // the host can spawn a real instance and the native RPC/accept path stays intact.
            if (_netObj != null)
            {
                try
                {
                    bool registered = false;
                    try { registered = nm().NetworkConfig.Prefabs.Contains(src); } catch { }
                    log?.LogInfo($"[reel:act] build netObj prefabHash={F(_netObj, "GlobalObjectIdHash")} registered={registered} " +
                                 $"spawned={_netObj.IsSpawned}");
                    if (!_netObj.IsSpawned) _netObj.Spawn();
                    log?.LogInfo($"[reel:act] build spawned={_netObj.IsSpawned} id={_netObj.NetworkObjectId} " +
                                 $"owner={_netObj.OwnerClientId}");
                }
                catch (Exception e) { log?.LogWarning($"[reel:act] build spawn failed (continuing unspawned): {e.Message}"); }
            }
            else log?.LogWarning("[reel:act] build: prefab has no NetworkObject");

            Subscribe(log);

            _built = true;
            if (!Live.Contains(this)) Live.Add(this);
            _state = $"built ({how}) spawned={(_netObj != null && _netObj.IsSpawned)}";
            log?.LogInfo($"[reel:act] build ok source={how} go='{PathOf(go.transform)}' comps=[{Short(CompTypes(go))}]");
            DumpFields(log, "built lookable", _lookable, 20);
            return true;
        }
        catch (Exception e)
        {
            _state = "build failed: " + e.Message;
            log?.LogError($"[reel:act] {_state}\n{e.StackTrace}");
            return false;
        }
        finally
        {
            // Nothing a failed attempt made may outlive it. The holder is scaffolding for as long as
            // the clone is inactive; a clone that never reached _built is a partial body, and leaving
            // it parented to the booth is what made every retry leak another one. Despawn first so a
            // clone that was already handed to Netcode does not stay spawned without its object.
            if (!_built)
            {
                try { if (_netObj != null && _netObj.IsSpawned) _netObj.Despawn(true); } catch { }
                if (go != null) { try { UnityEngine.Object.Destroy(go); } catch { } }
                if (ReferenceEquals(_go, go)) _go = null;
                _lookable = null; _ui = null; _interactable = null; _netObj = null;
                _prompt = null; _promptLabelOk = false;
            }
            if (holder != null) { try { UnityEngine.Object.Destroy(holder); } catch { } }
        }
    }

    // Remove any AlwaysFalseConstraint from the cloned chain (the template is deliberately
    // non-interactable). Every constraint component in the subtree is logged first, so the exact
    // set that decides CanInteract/CanLook is auditable.
    // Remove the template's own gameplay gates from the cloned chain. The reel borrows a scene
    // template to get the game's real lookable/interactable plumbing, but the template's rules are
    // about the object it was authored for:
    //   AlwaysFalseConstraint     the template is deliberately non-interactable (achievement totem)
    //   RoleInteractionConstraint a sacrifice altar only the Altered role may use; it refuses every
    //                             other player, which is what left our lookable 'situative' (refused)
    //                             and its prompt in the Error view in a map
    //   CooldownConstraint        the altar's own 5m50s cooldown, not the reel's (the reel has its
    //                             own one-spin-at-a-time guard)
    // The generic player gates (in-range, obstacle-in-front, can-interact, knockout, dead) stay, so
    // the booth still refuses exactly like any other interactable.
    private static bool StripTemplateGate(string typeName)
    {
        return typeName.IndexOf("AlwaysFalse", StringComparison.Ordinal) >= 0
            || typeName.IndexOf("RoleInteractionConstraint", StringComparison.Ordinal) >= 0
            || typeName.IndexOf("CooldownConstraint", StringComparison.Ordinal) >= 0;
    }

    private static void StripAlwaysFalse(IHostContext log, GameObject root)
    {
        try
        {
            var comps = root.GetComponentsInChildren<Component>(true);
            int stripped = 0;
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null) continue;
                string tn = TypeName(c);
                if (tn.IndexOf("Constraint", StringComparison.Ordinal) < 0) continue;
                bool gate = StripTemplateGate(tn);
                log?.LogInfo($"[reel:act] constraint '{PathOf(c.transform)}' type={tn} enabled={(c is Behaviour b ? b.enabled : true)}" +
                             $"{(gate ? " -> removed" : "")}");
                if (!gate) continue;
                try { UnityEngine.Object.DestroyImmediate(c); } catch { UnityEngine.Object.Destroy(c); }
                stripped++;
            }
            log?.LogInfo($"[reel:act] constraints stripped={stripped}");
        }
        catch (Exception e) { log?.LogWarning($"[reel:act] constraint strip failed: {e.Message}"); }
    }

    // The clone's lookable can-see converters keep a serialized reference to the template's
    // interactable constraints; after the refusal is stripped one of those references is dead and
    // the converter answers "cannot see" forever, which hides the prompt. Point every converter at
    // a live constraint of the same family (range / obstacle / player-can-interact) so the look
    // gate mirrors the interaction gate again.
    private static void RewireLookableConstraints(IHostContext log, GameObject root)
    {
        try
        {
            var comps = root.GetComponentsInChildren<InteractionSystem.Core.InteractionConstraint>(true);
            var live = new List<InteractionSystem.Core.InteractionConstraint>();
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null) continue;
                string tn = TypeName(c);
                if (tn.IndexOf("InteractorIsInRange", StringComparison.Ordinal) >= 0 ||
                    tn.IndexOf("ObstacleInFrontOfInteractor", StringComparison.Ordinal) >= 0 ||
                    tn.IndexOf("CanInteractPlayerInteractor", StringComparison.Ordinal) >= 0)
                    live.Add(c);
            }
            var convs = root.GetComponentsInChildren<InteractionSystem.Core.InteractionToLookableConstraintConverter>(true);
            log?.LogInfo($"[reel:act] rewire liveConstraints={live.Count} converters={convs.Length}");
            for (int i = 0; i < convs.Length; i++)
            {
                var conv = convs[i];
                if (conv == null) continue;
                if (live.Count == 0) { UnityEngine.Object.DestroyImmediate(conv); log?.LogWarning("[reel:act] rewire: no live constraint, converter removed"); continue; }
                var target = live[i % live.Count];
                SetF(conv, "_interactionConstraintToConvert", target);
                log?.LogInfo($"[reel:act] rewire converter[{i}] -> {TypeName(target)} '{PathOf(target.transform)}'");
            }
        }
        catch (Exception e) { log?.LogWarning($"[reel:act] rewire failed: {e.Message}"); }
    }

    private static string InvokeBool(object o, string method)
    {
        try
        {
            var m = o.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (m == null) return "no-method";
            return (m.Invoke(o, null) as bool?)?.ToString() ?? "null";
        }
        catch (TargetInvocationException e) { return "threw:" + (e.InnerException ?? e).GetType().Name; }
        catch (Exception e) { return "err:" + e.Message; }
    }

    // Diagnostic only: drive the lookable's own UI owner to the Active state through its public
    // entry point, to prove the game's world-space prompt (E + localized label) renders for it.
    private void ForceUi(IHostContext log, bool show)
    {
        try
        {
            if (_ui == null) { log?.LogWarning("[reel:act] forceui: not built"); return; }
            var st = Enum.Parse(typeof(InteractionSystem.Implementation.UI.DistantUIState), show ? "Active" : "Disabled");
            var m = _ui.GetType().GetMethod("SetUIState");
            if (m == null) { log?.LogWarning("[reel:act] forceui: SetUIState not found"); return; }
            var ps = m.GetParameters();
            object[] args = ps.Length == 2 ? new object[] { st, true } : new object[] { st };
            m.Invoke(_ui, args);
            log?.LogInfo($"[reel:act] forceui {st} state={F(_ui, "_uiState")}");
            var pres = All<InteractionSystem.Implementation.UI.DistantInteractionUIPresenter>();
            for (int i = 0; i < pres.Length; i++)
            {
                var p = pres[i];
                if (p == null || !InScene(p.gameObject)) continue;
                var av = F(p, "_activeView") as Component;
                if (av == null) continue;
                log?.LogInfo($"[reel:act] forceui activeView shown={F(av, "IsShown")} " +
                             $"owner={Descr(F(av, "_owner") as UnityEngine.Object)} " +
                             $"button='{(F(av, "_interactButtonTMP") as TMPro.TMP_Text)?.text}' " +
                             $"name='{(F(av, "_interactionNameTMP") as TMPro.TMP_Text)?.text}'");
            }
        }
        catch (Exception e) { log?.LogWarning($"[reel:act] forceui failed: {e.Message}"); }
    }

    private static Unity.Netcode.NetworkManager nm() => Unity.Netcode.NetworkManager.Singleton;

    // Finds the game's own minimal interactable chain (NetworkObject + BoxCollider +
    // LookableDistantInteractable + EmptyInteractable + 'Interaction WS UI'), preferring a loaded
    // prefab asset and one whose constraints do not contain an AlwaysFalseConstraint. Nothing is
    // taken from a live object: an asset is instantiated, a scene template is cloned.
    private static GameObject FindInteractablePrefab(IHostContext log, out string how)
    {
        how = "none";
        GameObject bestAsset = null, bestScene = null;
        string bestAssetHow = null, bestSceneHow = null;
        int found = 0;
        try
        {
            var gos = All<GameObject>();
            for (int i = 0; i < gos.Length; i++)
            {
                var go = gos[i];
                if (go == null) continue;
                var empty = go.GetComponent<InteractionSystem.Implementation.Interactables.EmptyInteractable>();
                var lk = go.GetComponent<InteractionSystem.Implementation.Lookables.LookableDistantInteractable>();
                var no = go.GetComponent<Unity.Netcode.NetworkObject>();
                if (empty == null || lk == null || no == null) continue;
                bool scene = InScene(go);
                // Never clone our own interactable: with two booths the second one would take the
                // first's clone (and its dead cross-references) as its template.
                if (PathOf(go.transform).IndexOf(LobbyPropFactory.RootName, StringComparison.Ordinal) >= 0) continue;
                bool alwaysFalse = HasConstraint(go, "AlwaysFalse");
                bool box = go.GetComponent<BoxCollider>() != null;
                string ui = F(lk, "_ui") != null ? "ui" : "noUi";
                if (found++ < 14)
                    log?.LogInfo($"[reel:act] template candidate '{PathOf(go.transform)}' scene={scene} active={go.activeSelf} " +
                                 $"box={box} alwaysFalse={alwaysFalse} {ui} layer={go.layer} prefabHash={F(no, "GlobalObjectIdHash")} " +
                                 $"comps=[{Short(CompTypes(go))}]");
                if (!box) continue;   // the AlwaysFalse refusal is stripped before activation
                if (!scene && bestAsset == null) { bestAsset = go; bestAssetHow = "asset prefab '" + go.name + "'"; }
                if (scene && bestScene == null) { bestScene = go; bestSceneHow = "scene template '" + PathOf(go.transform) + "'"; }
            }
        }
        catch (Exception e) { log?.LogWarning($"[reel:act] template scan: {e.Message}"); }
        log?.LogInfo($"[reel:act] template candidates={found} asset={(bestAsset != null ? bestAsset.name : "-")} " +
                     $"scene={(bestScene != null ? bestScene.name : "-")}");
        if (bestAsset != null) { how = bestAssetHow; return bestAsset; }
        if (bestScene != null) { how = bestSceneHow; return bestScene; }
        return null;
    }

    private static bool HasConstraint(GameObject go, string token)
    {
        try
        {
            var cs = go.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < cs.Length; i++)
            {
                if (cs[i] == null) continue;
                if (TypeName(cs[i]).IndexOf(token, StringComparison.Ordinal) >= 0) return true;
            }
        }
        catch { }
        return false;
    }

    // ---- interaction provider -----------------------------------------------------------
    //
    // The lookable only needs a provider whose `Interaction` is the local player's own TapInteraction
    // (key 'e'): the game's InteractableInputHandler binds that interaction while the looker holds
    // the lookable, and the key's callback runs Interaction -> Interactable.InteractedLocally.
    //
    // The provider a cloned lookable ships with is never Construct()ed: Construct(IPlayerMediator)
    // is private, takes the local player's mediator, and only the player's own prefab goes through
    // it. Its `_interaction` is therefore null, and its InitializeInteraction() (which reads
    // `_interactionData`) throws NullReferenceException. That NRE was ours - the old wiring called
    // InitializeInteraction on the unconstructed clone. It is not called any more. The clone's dead
    // provider is removed while the clone is still inactive (before the Awake that logs
    // "Interaction Provider Not Constructed", i.e. an [Error] line), and the lookable is pointed at
    // a provider the game constructed itself for this local player.
    private Component _provider;
    private string _providerLogKey;

    // Every live booth interactable, in build order. Requests address booth 1 by default; a
    // trailing '@<n>' addresses another (two saved anchors share the request file).
    internal static readonly List<ReelInteraction> Live = new List<ReelInteraction>();
    internal static ReelInteraction At(int oneBased)
    {
        int i = oneBased - 1;
        return (i >= 0 && i < Live.Count) ? Live[i] : null;
    }

    // The prompt's action label must come from a localisation entry that actually resolves. The
    // clone inherits the template's serialized value; when that resolves (observed: "interact") it
    // is kept as-is. When it does not, the value is copied from another live prompt of the SAME
    // native family whose label does resolve - never a translation key of our own invention, and
    // never a TMP text patched after localisation failed (that is exactly what produced the
    // "NO TRANSLATION FOUND FOR 'PRIMARY INTERACT' IN 'INTERACTABLE'" row in the HUD list).
    private void InitPromptLabel(IHostContext log, Component prompt)
    {
        _promptLabelOk = false;
        _promptLabelSource = "none";
        if (prompt == null) return;
        object mine = F(prompt, "_currentActionName");
        string mineText = ResolveLabel(mine);
        log?.LogInfo($"[reel:act] prompt label self pair={Loc(mine)} resolves={LabelOk(mineText)} " +
                     $"text='{Short(mineText)}'");
        if (LabelOk(mineText))
        {
            _promptLabelOk = true;
            _promptLabelSource = "template";
            return;
        }
        var uis = All<InteractionSystem.Implementation.UI.DistantInteractionWorldSpaceUI>();
        for (int i = 0; i < uis.Length; i++)
        {
            var other = uis[i];
            if (other == null || !InScene(other.gameObject)) continue;
            if (!other.gameObject.activeInHierarchy) continue;
            if (_go != null && other.gameObject == _go) continue;      // never our own
            var pr = F(other, "_prompt") as Component;
            if (pr == null || pr.Equals(prompt)) continue;
            object theirs = F(pr, "_currentActionName");
            string text = ResolveLabel(theirs);
            if (!LabelOk(text)) continue;
            SetF(prompt, "_currentActionName", theirs);
            try
            {
                // The presenter copies the localised field into its TMP on its own update; nudge it
                // so the world prompt shows the label this frame instead of the template's.
                var upd = prompt.GetType().GetMethod("OnUpdated", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                upd?.Invoke(prompt, null);
            }
            catch (Exception e) { log?.LogInfo($"[reel:act] prompt label OnUpdated: {e.Message}"); }
            string afterText = ResolveLabel(F(prompt, "_currentActionName"));
            _promptLabelOk = LabelOk(afterText);
            _promptLabelSource = "copied:'" + PathOf(other.transform) + "'";
            log?.LogInfo($"[reel:act] prompt label copied source='{PathOf(other.transform)}' " +
                         $"pair={Loc(theirs)} text='{Short(afterText)}' ok={_promptLabelOk}");
            if (_promptLabelOk) return;
        }
        _promptLabelSource = "unresolved";
        log?.LogWarning("[reel:act] prompt label unresolved: no resolving stock interact label in this scene; " +
                        "the native prompt cannot display, so the drawn prompt is used instead (never both)");
    }

    private static string ResolveLabel(object localized)
    {
        if (localized == null) return "";
        try
        {
            var m = localized.GetType().GetMethod("GetLocalizedString", Type.EmptyTypes);
            return m != null ? (m.Invoke(localized, null) as string ?? "") : "";
        }
        catch (TargetInvocationException) { return ""; }
        catch { return ""; }
    }

    private static bool LabelOk(string text) =>
        !string.IsNullOrEmpty(text) &&
        text.IndexOf("NO TRANSLATION FOUND", StringComparison.OrdinalIgnoreCase) < 0;

    // Census of the live interactable clones: every LookableDistantInteractable in the scene, split
    // into the ones under a reel booth and everything else, plus any object that carries our
    // interactable's name without being under a booth (a leftover clone from a previous generation).
    internal static void CensusClones(IHostContext log)
    {
        if (log == null) return;
        try
        {
            var all = All<InteractionSystem.Implementation.Lookables.LookableDistantInteractable>();
            int inScene = 0, reelOwned = 0, others = 0, leftover = 0;
            var sb = new StringBuilder();
            for (int i = 0; i < all.Length; i++)
            {
                var lk = all[i];
                if (lk == null || !InScene(lk.gameObject)) continue;
                inScene++;
                string path = PathOf(lk.transform);
                bool reel = path.IndexOf(LobbyPropFactory.RootName, StringComparison.Ordinal) >= 0;
                if (reel) reelOwned++; else others++;
                bool stray = !reel && (path.IndexOf("Reel of Fortune Interactable", StringComparison.Ordinal) >= 0 ||
                                       path.IndexOf("ReelInteractable", StringComparison.Ordinal) >= 0);
                if (stray) leftover++;
                if (sb.Length < 900)
                    sb.Append($"\n  lookable '{path}' reelOwned={reel} stray={stray} " +
                              $"active={lk.gameObject.activeInHierarchy} layer={lk.gameObject.layer}");
            }
            var sb2 = new StringBuilder();
            for (int i = 0; i < Live.Count; i++)
                sb2.Append($"\n  clone[{i + 1}] built={Live[i].Built} nativePrompt={Live[i].NativePromptLive} " +
                           $"label={Live[i].PromptLabelSource} state='{Live[i]._state}' accepted={Live[i]._accepted}");
            log.LogInfo($"[reel:act] clone census lookables={all.Length} inScene={inScene} " +
                        $"reelOwned={reelOwned} others={others} leftoverClones={leftover} live={Live.Count}{sb}{sb2}");
        }
        catch (Exception e) { log.LogWarning($"[reel:act] clone census failed: {e.Message}"); }
    }

    private void WireProvider(IHostContext log)
    {
        try
        {
            if (_lookable == null) return;
            var stock = ResolveProvider(log);
            if (stock == null) return;
            if (ReferenceEquals(F(_lookable, "_interactionProvider") as Component, stock)) return;
            SetF(_lookable, "_interactionProvider", stock);
            log?.LogInfo($"[reel:act] provider -> type={TypeName(stock)} '{PathOf(stock.transform)}' " +
                         $"interaction={Descr(F(stock, "Interaction") as UnityEngine.Object)} " +
                         $"key='{F(F(stock, "Interaction"), "InteractionKeyName")}' " +
                         $"constructed={F(stock, "_isConstructed")}");
        }
        catch (Exception e) { log?.LogWarning($"[reel:act] provider wire failed: {e.Message}"); }
    }

    // The provider this lookable uses, resolved from the local player's own components. Ranking:
    // under the player and not an inventory/handheld item first (the player's own interactables are
    // respawned with the player and are never destroyed by dropping an item), then any other
    // component under the player, then a scene provider as a last resort. Only providers that
    // already resolve an interaction with key 'e' qualify - an unresolved provider is exactly what
    // produced the NRE.
    private Component ResolveProvider(IHostContext log)
    {
        try
        {
            if (_provider != null)
            {
                if (Living(_provider) && Resolved(_provider)) return _provider;
                _provider = null;
            }
            var player = ReelLogic.FindLocalPlayerEntity();
            Component best = null;
            int bestRank = int.MaxValue;
            string bestWhy = "-";
            var provs = All<InputSystem.Core.Interactions.InteractionProvider>();
            for (int i = 0; i < provs.Length; i++)
            {
                var p = provs[i];
                if (p == null || !InScene(p.gameObject)) continue;
                if (_go != null && p.gameObject == _go) continue;      // never the clone's own provider
                if (!Resolved(p)) continue;
                string path = PathOf(p.transform);
                bool underPlayer = player != null && Under(p.transform, player.transform);
                bool inventory = path.IndexOf("Inventory Items", StringComparison.Ordinal) >= 0
                              || path.IndexOf("Handheld", StringComparison.Ordinal) >= 0;
                int rank = underPlayer ? (inventory ? 2 : 0) : 3;
                if (rank >= bestRank) continue;
                best = p; bestRank = rank;
                bestWhy = underPlayer ? (inventory ? "player/inventory" : "player") : "scene";
            }
            _provider = best;
            if (best != null)
            {
                string key = bestWhy + "|" + PathOf(best.transform);
                if (key != _providerLogKey)
                {
                    _providerLogKey = key;
                    log?.LogInfo($"[reel:act] provider resolved player={(player != null)} via={bestWhy} " +
                                 $"type={TypeName(best)} '{PathOf(best.transform)}' " +
                                 $"interaction={Descr(F(best, "Interaction") as UnityEngine.Object)} " +
                                 $"key='{F(F(best, "Interaction"), "InteractionKeyName")}'");
                }
            }
            return best;
        }
        catch (Exception e) { log?.LogWarning($"[reel:act] provider resolve failed: {e.Message}"); return null; }
    }

    private static bool Living(Component c)
    {
        try { return c != null && c.gameObject != null && c.gameObject.activeInHierarchy; } catch { return false; }
    }

    private static bool Resolved(Component p)
    {
        try
        {
            var inter = F(p, "Interaction") as UnityEngine.Object;
            if (inter == null) return false;
            string key = F(inter, "InteractionKeyName") as string;
            return string.IsNullOrEmpty(key) || key.Equals("e", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool Under(Transform child, Transform parent)
    {
        try
        {
            var t = child; int guard = 0;
            while (t != null && guard++ < 24) { if (t == parent) return true; t = t.parent; }
        }
        catch { }
        return false;
    }

    private float _nextProviderCheck;

    // Pre-activation provider settle (see Build): drop every InteractionProvider the clone carries
    // (never Construct()ed -> its Awake logs an [Error], and its InitializeInteraction NREs) and
    // point the lookable at the resolved, game-constructed provider instead.
    private void PrepareProvider(IHostContext log, GameObject go)
    {
        try
        {
            var lookable = go.GetComponent<InteractionSystem.Implementation.Lookables.LookableDistantInteractable>();
            var stock = ResolveProvider(log);
            if (lookable != null && stock != null)
            {
                SetF(lookable, "_interactionProvider", stock);
                log?.LogInfo($"[reel:act] provider settle -> type={TypeName(stock)} '{PathOf(stock.transform)}' " +
                             $"key='{F(F(stock, "Interaction"), "InteractionKeyName")}'");
            }
            var provs = go.GetComponentsInChildren<InputSystem.Core.Interactions.InteractionProvider>(true);
            for (int i = 0; i < (provs?.Length ?? 0); i++)
            {
                var p = provs[i];
                if (p == null) continue;
                string path = PathOf(p.transform);
                object was = F(p, "_isConstructed");
                // DestroyImmediate, not Destroy: Destroy defers to the end of the frame, so the
                // component would still Awake when the clone activates in this same frame. The
                // object is inactive and still under the throw-away holder, so this is safe.
                UnityEngine.Object.DestroyImmediate(p);
                log?.LogInfo($"[reel:act] provider settle: dropped clone provider '{path}' " +
                             $"constructed={was} (it is never Construct()ed against the player)");
            }
        }
        catch (Exception e) { log?.LogWarning($"[reel:act] provider settle failed: {e.Message}"); }
    }

    private void ProviderGuard(IHostContext log)
    {
        if (!_built || _lookable == null) return;
        if (Time.unscaledTime < _nextProviderCheck) return;
        _nextProviderCheck = Time.unscaledTime + 0.5f;
        try
        {
            var prov = F(_lookable, "_interactionProvider") as Component;
            bool dead = prov == null || prov.gameObject == null;
            bool unresolved = !dead && !(F(prov, "Interaction") is UnityEngine.Object io && io != null);
            if (!dead && !unresolved) return;
            // A player respawn replaces the player root, and with it the provider component; a
            // dropped item destroys a handheld provider. Re-resolve against the live player.
            _provider = null;
            log?.LogInfo($"[reel:act] provider guard: dead={dead} unresolved={unresolved}, re-resolving");
            WireProvider(log);
        }
        catch (Exception e) { log?.LogWarning($"[reel:act] provider guard failed: {e.Message}"); }
    }

    // True when this booth's native interactable is live and owns the E key. LobbyPropRuntime reads
    // it to stand down its raw-key fallback: one press must have exactly one handler.
    internal bool HandlesKey
    {
        get
        {
            try
            {
                if (!_built || _lookable == null) return false;
                var prov = F(_lookable, "_interactionProvider") as Component;
                return prov != null && prov.gameObject != null && Resolved(prov);
            }
            catch { return false; }
        }
    }

    private static object Invoke(object o, string method, params object[] args)
    {
        try
        {
            var m = o.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (m == null) return null;
            var ps = m.GetParameters();
            if (ps.Length != (args?.Length ?? 0)) args = new object[ps.Length];
            return m.Invoke(o, args);
        }
        catch (TargetInvocationException e) { _log?.LogWarning($"[reel:act] {method} threw: {(e.InnerException ?? e).Message}"); return null; }
    }

    // Every field/property of an object's proxy types (including nulls and value types), so two
    // instances of the same component can be diffed instead of guessed about.
    private static string FieldText(object o)
    {
        if (o == null) return "-";
        var sb = new StringBuilder();
        try
        {
            for (var t = o.GetType(); t != null && t != typeof(object) && t != typeof(UnityEngine.Object) &&
                 t != typeof(Component) && t != typeof(Behaviour) && t != typeof(MonoBehaviour); t = t.BaseType)
            {
                foreach (var pi in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (pi.GetIndexParameters().Length != 0) continue;
                    if (!pi.Name.StartsWith("_", StringComparison.Ordinal)) continue;
                    object v = null;
                    try { v = pi.GetValue(o); } catch { continue; }
                    sb.Append(pi.Name).Append('=');
                    if (v is UnityEngine.Object uo) sb.Append(Descr(uo));
                    else sb.Append(v == null ? "null" : v.ToString());
                    sb.Append('(').Append(v == null ? "null" : TypeName(v)).Append(") ");
                }
            }
        }
        catch { }
        return sb.ToString();
    }

    // Every InteractionProvider in the scene, with the interaction it resolved. Diagnostics only.
    private void Providers(IHostContext log)
    {
        var ps = All<InputSystem.Core.Interactions.InteractionProvider>();
        int shown = 0;
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            if (p == null || !InScene(p.gameObject)) continue;
            if (shown++ >= 12) continue;
            try
            {
                var inter = F(p, "Interaction");
                log.LogInfo($"[reel:act] providers[{shown - 1}] '{PathOf(p.transform)}' type={TypeName(p)} " +
                            $"interaction={Descr(inter as UnityEngine.Object)} key='{F(inter, "InteractionKeyName")}' " +
                            $"constructed={F(p, "_isConstructed")} raw={Descr(F(p, "_interaction") as UnityEngine.Object)}");
            }
            catch (Exception e) { log.LogWarning($"[reel:act] providers[{shown - 1}]: {e.Message}"); }
        }
        log.LogInfo($"[reel:act] providers total={ps.Length} inScene={shown}");
    }

    // Manually perform the looker's hand-off (lookable.OnStartLooking/OnStopLooking) so the
    // difference between it and the looker's own tick is observable in one request.
    private void LookHandoff(IHostContext log, bool start)
    {
        try
        {
            var looker = LocalLooker();
            if (looker == null || _lookable == null) { log?.LogWarning("[reel:act] handoff: no looker/lookable"); return; }
            var m = _lookable.GetType().GetMethod(start ? "OnStartLooking" : "OnStopLooking",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (m == null) { log?.LogWarning("[reel:act] handoff: method not found"); return; }
            m.Invoke(_lookable, new object[] { looker });
            log?.LogInfo($"[reel:act] handoff {(start ? "OnStartLooking" : "OnStopLooking")} done");
            HandlerState(log, "after handoff");
        }
        catch (TargetInvocationException e) { log?.LogWarning($"[reel:act] handoff threw: {(e.InnerException ?? e).Message}"); }
        catch (Exception e) { log?.LogWarning($"[reel:act] handoff failed: {e.Message}"); }
    }

    private void HandlerState(IHostContext log, string tag)
    {
        try
        {
            var hs = All<InputSystem.Interaction.InteractableInputHandler>();
            for (int i = 0; i < hs.Length; i++)
            {
                var h = hs[i];
                if (h == null || !InScene(h.gameObject)) continue;
                var pi = F(h, "_previousLookableInteraction");
                log.LogInfo($"[reel:act] {tag} handler prevLookable={Descr(F(h, "_previousLookableInteractable") as UnityEngine.Object)} " +
                            $"prevInteraction={Descr(pi as UnityEngine.Object)} key='{F(pi, "InteractionKeyName")}' " +
                            $"canInteractCached={F(h, "_canInteractCached")} " +
                            $"interactor.current={Descr(F(F(h, "_interactor"), "_currentInteractable") as UnityEngine.Object)}");
                break;
            }
        }
        catch (Exception e) { log.LogWarning($"[reel:act] {tag} handler: {e.Message}"); }
    }

    // Invoke the handler's own interaction callback (the method its TapInteraction binds to the
    // key). This is the last managed step of the native path: if it fires exactly one
    // InteractedLocally, the chain is complete and only the device-level key event is missing.
    // Invoke the handler's own interaction callback - the method its TapInteraction binds to the
    // key - but only when the handler actually has an interaction bound. OnInteractionPerformed
    // dereferences _previousLookableInteraction/_previousLookableInteractable, which the handler
    // only fills while the looker holds a lookable; called outside that window it is a guaranteed
    // NullReferenceException in the game's own code, and it was the second NRE this path produced.
    // So the precondition is reported instead of being handed a null and pretending to be a
    // keypress that could not have happened.
    private void HandlerFire(IHostContext log)
    {
        try
        {
            var hs = All<InputSystem.Interaction.InteractableInputHandler>();
            for (int i = 0; i < hs.Length; i++)
            {
                var h = hs[i];
                if (h == null || !InScene(h.gameObject)) continue;
                HandlerState(log, "before fire");
                var bound = F(h, "_previousLookableInteraction") as UnityEngine.Object;
                if (bound == null)
                {
                    var looker = LocalLooker();
                    log?.LogWarning("[reel:act] press: not delivered to the reel - the input handler has no " +
                                    "interaction bound, i.e. no lookable is active. " +
                                    $"looker current={Descr(F(looker, "_currentActiveLookable") as UnityEngine.Object)} " +
                                    "(the handler only binds while the looker holds a lookable, so there is " +
                                    "nothing for E to fire)");
                    return;
                }
                int before = _accepted;
                Invoke(h, "OnInteractionPerformed");
                log?.LogInfo($"[reel:act] handlerfire invoked accepted {before}->{_accepted} " +
                             $"key='{F(bound, "InteractionKeyName")}' canInteractCached={F(h, "_canInteractCached")}");
                HandlerState(log, "after fire");
                return;
            }
            log?.LogWarning("[reel:act] handlerfire: no scene handler");
        }
        catch (Exception e) { log?.LogWarning($"[reel:act] handlerfire failed: {e.Message}"); }
    }

    // ---- per-frame trace ----------------------------------------------------------------

    private float _frameUntil;
    private int _frameCount;
    private string _frameRef;

    private void StartFrameLog(IHostContext log, string seconds)
    {
        float s = 6f;
        try { s = float.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture); } catch { }
        _frameUntil = Time.unscaledTime + Mathf.Clamp(s, 1f, 30f);
        _frameCount = 0;
        _frameRef = null;
        log?.LogInfo($"[reel:act] frames begin for {s:F1}s");
    }

    // One line per frame: the looker's candidate set, the lookable it holds, our object's own
    // verdicts, and a stock interactable in the same frame. The promotion condition is the column
    // that differs between the two.
    private void FrameTick(IHostContext log)
    {
        if (_frameUntil <= 0f || Time.unscaledTime > _frameUntil) return;
        if (_lookable == null) return;
        try
        {
            var looker = LocalLooker();
            if (looker == null) return;
            if (_frameCount++ > 900) { _frameUntil = 0f; log?.LogInfo("[reel:act] frames end (cap)"); return; }

            var cur = F(looker, "_lookablesInRadiusCurrentUpdate");
            int n = Count(cur);
            var active = F(looker, "_currentActiveLookable") as UnityEngine.Object;
            var frame = F(looker, "_currentFrameLookable") as UnityEngine.Object;
            var prev = F(looker, "_previousActiveLookable") as UnityEngine.Object;
            var ours = Probe(log, looker, _lookable);

            // Reference: the closest stock lookable that is in radius but not ours.
            UnityEngine.Object reference = null;
            float best = float.MaxValue;
            var centre = F(_lookable, "_lookableCenter") as Transform;
            for (int i = 0; i < n && i < 64; i++)
            {
                var o = At(cur, i) as Component;
                if (o == null || o == _lookable) continue;
                var c = F(o, "_lookableCenter") as Transform;
                if (c == null || centre == null) continue;
                float d = Vector3.Distance(c.position, centre.position);
                if (d < best) { best = d; reference = o; }
            }
            string refText = reference != null ? Probe(log, looker, reference) : "none";
            string refName = reference != null ? reference.name : "-";

            string handler = "-";
            var hs = All<InputSystem.Interaction.InteractableInputHandler>();
            for (int i = 0; i < hs.Length; i++)
            {
                var h = hs[i];
                if (h == null || !InScene(h.gameObject)) continue;
                var pi = F(h, "_previousLookableInteraction");
                handler = $"prev={Descr(F(h, "_previousLookableInteractable") as UnityEngine.Object)} key='{F(pi, "InteractionKeyName")}'";
                break;
            }

            log.LogInfo($"[reel:frame] {_frameCount} radius={n} active={(active != null ? active.name : "-")} " +
                        $"frame={(frame != null ? frame.name : "-")} prev={(prev != null ? prev.name : "-")} | " +
                        $"ours {ours} | ref '{refName}' {refText} | {handler}");
        }
        catch (Exception e) { _frameUntil = 0f; log?.LogWarning($"[reel:act] frames failed: {e.Message}"); }
    }

    // The per-frame verdicts for one lookable: is it in the looker's radius arrays, what state its
    // WS UI is in, whether CanLook passes, the measured facing dot against both the looker's and the
    // lookable's own threshold, and whether its interactable can be interacted with right now.
    private string Probe(IHostContext log, InteractionSystem.Implementation.Lookers.DistantInteractableLooker looker, UnityEngine.Object lookable)
    {
        try
        {
            var l = lookable as InteractionSystem.Implementation.Lookables.LookableDistantInteractable;
            if (l == null) return "-";
            var cur = F(looker, "_lookablesInRadiusCurrentUpdate");
            var pre = F(looker, "_lookablesInRadiusPreviousUpdate");
            bool inCur = Contains(cur, l), inPre = Contains(pre, l);
            var ui = F(l, "_ui");
            string state = F(ui, "_uiState")?.ToString() ?? "-";
            var canLook = InvokeBoolArg(looker, "CanLook", l);
            var cantSee = InvokeBoolArg(looker, "IsAtLeastOneLookableConstraintCantSee", l);
            var situative = InvokeBoolArg(looker, "IsCantSeeBecauseOfSituativeConstraints", l);
            var origin = F(F(looker, "_interactor"), "_lookOrigin") as Transform;
            var centre = F(l, "_lookableCenter") as Transform;
            float dist = -1f, dot = -2f;
            if (origin != null && centre != null)
            {
                Vector3 to = centre.position - origin.position;
                dist = to.magnitude;
                if (dist > 0.001f) dot = Vector3.Dot(origin.forward, to / dist);
            }
            var ia = F(l, "_interactable") as InteractionSystem.Core.Interactable;
            string canInteract = "-";
            if (ia != null)
            {
                try
                {
                    var m = ia.GetType().GetMethod("CanInteract", BindingFlags.Public | BindingFlags.Instance, null,
                        new[] { typeof(InteractionSystem.Core.Interactor), typeof(InteractionSystem.Core.InteractionConstraint).MakeByRefType() }, null);
                    if (m != null)
                    {
                        var args = new object[] { F(looker, "_interactor"), null };
                        canInteract = (m.Invoke(ia, args) as bool?)?.ToString() ?? "null";
                        if (args[1] != null) canInteract += "/" + TypeName(args[1]);
                    }
                }
                catch (TargetInvocationException e) { canInteract = "threw:" + (e.InnerException ?? e).GetType().Name; }
                catch (Exception e) { canInteract = "err"; }
            }
            return $"in=({inCur},{inPre}) state={state} canLook={canLook} cantSee={cantSee} situative={situative} " +
                   $"dist={dist:F2} dot={dot:F3} ovr=({F(l, "_overrideRequireFacingDot")},{F(l, "_overridenRequiredFacingDot")}) " +
                   $"lookerDot={F(looker, "_requiredFacingDot")} canInteract={canInteract} " +
                   $"key='{F(F(F(l, "_interactionProvider"), "Interaction"), "InteractionKeyName")}'";
        }
        catch (Exception e) { return "err:" + e.Message; }
    }

    private static string InvokeBoolArg(object o, string method, object arg)
    {
        try
        {
            var m = o.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (m == null) return "no-method";
            var r = m.Invoke(o, new[] { arg });
            return r is bool b ? b.ToString() : (r?.ToString() ?? "null");
        }
        catch (TargetInvocationException e) { return "threw:" + (e.InnerException ?? e).GetType().Name; }
        catch (Exception e) { return "err"; }
    }

    private static bool Contains(object array, object item)
    {
        try
        {
            int n = Count(array);
            for (int i = 0; i < n; i++)
            {
                var o = At(array, i);
                if (ReferenceEquals(o, item)) return true;
                if (o is UnityEngine.Object uo && item is UnityEngine.Object ui && uo == ui) return true;
            }
        }
        catch { }
        return false;
    }

    // Where the interaction anchors actually are after the booth moved: the trigger collider (the
    // looker's can-see raycast target), the look centre and the WS-UI anchor must all be at the new
    // pose, not the old one.
    internal void ReportAnchors(IHostContext log, string tag)
    {
        try
        {
            if (_go == null) { log?.LogInfo($"[reel:act] anchors {tag}: interactable absent"); return; }
            var box = F(_lookable, "_collider") as Collider;
            var centre = F(_lookable, "_lookableCenter") as Transform;
            var ui = _ui != null ? _ui.transform : null;
            log?.LogInfo($"[reel:act] anchors {tag}: interactable='{PathOf(_go.transform)}' " +
                         $"collider={(box != null ? V(box.bounds.center) + "/" + V(box.bounds.size) : "-")} " +
                         $"lookCentre={(centre != null ? V(centre.position) : "-")} " +
                         $"wsUI={(ui != null ? V(ui.position) : "-")}");
        }
        catch (Exception e) { log?.LogWarning($"[reel:act] anchors {tag}: {e.Message}"); }
    }

    // Rebuild the interactable after the booth it was parented to was re-created (scene change,
    // placement restore). The clone lives under the booth root, so it dies with it.
    internal void EnsureBuilt(IHostContext log, LobbyPropRuntime rt)
    {
        try
        {
            if (_built && _go == null)
            {
                _built = false; _subscribed = false;
                _lookable = null; _ui = null; _interactable = null; _netObj = null;
                _provider = null; _providerLogKey = null;
                _state = "stale (booth recreated)";
                _nextBuildAttempt = 0f; _retryState = "";
                log?.LogInfo("[reel:act] booth recreated; interactable will be rebuilt");
            }
            if (_built || rt == null || rt.Root == null) return;
            // A lookable cannot be built without a provider that resolves the local player's
            // interaction: with none assigned the game's handler never binds a key, and a lookable
            // whose provider field is null has nothing to offer the looker. In a map the player (and
            // its providers) can spawn a few seconds after the booth, so wait instead of building
            // a dead interactable. One line per 5 s while waiting.
            if (ResolveProvider(null) == null)
            {
                if (Time.unscaledTime >= _nextWaitLog)
                {
                    _nextWaitLog = Time.unscaledTime + 5f;
                    log?.LogInfo($"[reel:act] build deferred: no provider resolving the local player's interaction " +
                                 $"yet (player={(ReelLogic.FindLocalPlayerEntity() != null)}) scene='{Or(SceneName())}'");
                }
                return;
            }
            // A failing build used to be retried every frame, and every attempt made and leaked a
            // holder and a clone. Bound the retry: one attempt per BuildRetrySeconds, and the reason is
            // announced once per distinct failure state instead of once per frame.
            if (Time.unscaledTime < _nextBuildAttempt) return;
            if (!Build(log, rt))
            {
                _nextBuildAttempt = Time.unscaledTime + BuildRetrySeconds;
                if (_retryState != _state)
                {
                    _retryState = _state;
                    log?.LogInfo($"[reel:act] build failed ('{_state}'); next attempt in {BuildRetrySeconds:0}s");
                }
            }
        }
        catch (Exception e) { log?.LogWarning($"[reel:act] ensure built failed: {e.Message}"); }
    }

    private float _nextWaitLog;

    private static string SceneName()
    {
        try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; } catch { return ""; }
    }

    private static string Or(string s) => string.IsNullOrEmpty(s) ? "-" : s;

    private void Subscribe(IHostContext log)
    {
        if (_interactable == null || _subscribed) return;
        try
        {
            _interacted = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<InteractionSystem.Core.Interactor>>(
                new Action<InteractionSystem.Core.Interactor>(OnInteracted));
            _interactable.add_InteractedLocally(_interacted);
            _subscribed = true;
            log?.LogInfo("[reel:act] subscribed to Interactable.InteractedLocally");
        }
        catch (Exception e) { log?.LogWarning($"[reel:act] subscribe failed: {e.Message}"); }
    }

    private void OnInteracted(InteractionSystem.Core.Interactor interactor)
    {
        try
        {
            _accepted++;
            var ihost = _log;
            string who = interactor != null ? PathOf(interactor.transform) : "null";
            ihost?.LogInfo($"[reel:act] native interaction accepted n={_accepted} interactor='{who}'");
            if (!_filtered(interactor))
            {
                ihost?.LogInfo("[reel:act] accepted interaction ignored: not the local player's interactor");
                return;
            }
            _rt?.NativeInteract(ihost, who);
        }
        catch (Exception e) { _log?.LogWarning($"[reel:act] OnInteracted threw: {e.Message}"); }
    }

    private static bool _filtered(InteractionSystem.Core.Interactor interactor)
    {
        try
        {
            if (interactor == null) return false;
            var player = ReelLogic.FindLocalPlayerEntity();
            if (player == null) return false;
            var t = interactor.transform;
            int guard = 0;
            while (t != null && guard++ < 12)
            {
                if (t == player.transform) return true;
                if (t.name != null && t.name.StartsWith("LobbyPlayer_Separated", StringComparison.Ordinal)) return true;
                t = t.parent;
            }
            return true;   // the event is on our own interactable; do not drop it for a name mismatch
        }
        catch { return true; }
    }

    private static IHostContext _log;
    private LobbyPropRuntime _rt;

    internal void Teardown(IHostContext log)
    {
        // Drop our own event subscription before the object goes: the delegate closes over this
        // instance and a hot reload must not leave a callable onto a dead generation.
        if (_subscribed && _interactable != null && _interacted != null)
        {
            try
            {
                var m = _interactable.GetType().GetMethod("remove_InteractedLocally");
                if (m != null) { m.Invoke(_interactable, new object[] { _interacted }); log?.LogInfo("[reel:act] unsubscribed from Interactable.InteractedLocally"); }
                else log?.LogInfo("[reel:act] no remove_InteractedLocally accessor; subscription dies with the object");
            }
            catch (Exception e) { log?.LogWarning($"[reel:act] unsubscribe failed: {e.Message}"); }
        }
        _subscribed = false;
        _interacted = null;
        _prompt = null;
        _promptLabelOk = false;
        _promptLabelSource = "none";
        try { if (_netObj != null && _netObj.IsSpawned) _netObj.Despawn(true); } catch { }
        try { if (_go != null) UnityEngine.Object.Destroy(_go); } catch { }
        if (_lookable != null) { try { SetF(_lookable, "_interactionProvider", null); } catch { } }
        _go = null; _lookable = null; _ui = null; _interactable = null; _netObj = null;
        _built = false; _state = "torn down";
        _nextBuildAttempt = 0f; _retryState = "";
        Live.Remove(this);
        log?.LogInfo("[reel:act] teardown");
    }

    private static void SetF(object o, string name, object value)
    {
        if (o == null) return;
        try
        {
            for (var t = o.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                var pi = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (pi != null && pi.CanWrite) { pi.SetValue(o, value); return; }
                var fi = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (fi != null) { fi.SetValue(o, value); return; }
            }
            var any = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (any != null && any.CanWrite) any.SetValue(o, value);
        }
        catch (Exception e) { _log?.LogWarning($"[reel:act] set {name} failed: {e.Message}"); }
    }

    // Which lookable the local looker currently holds, what the presenter is showing, and how the
    // camera sits relative to the look centre. Diagnostics only - no prompt path depends on it.
    internal void DumpState(IHostContext log, LobbyPropRuntime rt)
    {
        try
        {
            log.LogInfo($"[reel:act] state built={_built} accepted={_accepted} '{_state}' source={_source}");
            if (_go != null)
            {
                var cam = ScreenShot.FindGameCamera(log);
                var centre = F(_lookable, "_lookableCenter") as Transform;
                Vector3 eye = cam != null ? cam.transform.position : Vector3.zero;
                Vector3 fwd = cam != null ? cam.transform.forward : Vector3.zero;
                float dist = centre != null ? Vector3.Distance(eye, centre.position) : -1f;
                float dot = centre != null && dist > 0.001f ? Vector3.Dot(fwd, (centre.position - eye) / dist) : -2f;
                log.LogInfo($"[reel:act] state pose go='{PathOf(_go.transform)}' world={V(_go.transform.position)} " +
                            $"centre={(centre != null ? V(centre.position) : "-")} eye={V(eye)} fwd={V(fwd)} dist={dist:F2} dot={dot:F3} " +
                            $"layer={_go.layer} spawned={(_netObj != null && _netObj.IsSpawned)} " +
                            $"uiState={F(_ui, "_uiState")}");
                var looker = LocalLooker();
                if (looker != null)
                {
                    var list = F(looker, "_lookablesInRadiusCurrentUpdate");
                    var names = new StringBuilder();
                    int n = Count(list);
                    for (int i = 0; i < n && i < 12; i++)
                    {
                        var o = At(list, i) as UnityEngine.Object;
                        if (o != null) names.Append(o.name).Append(' ');
                    }
                    log.LogInfo($"[reel:act] state looker current={Descr(F(looker, "_currentActiveLookable") as UnityEngine.Object)} " +
                                $"frame={Descr(F(looker, "_currentFrameLookable") as UnityEngine.Object)} " +
                                $"prev={Descr(F(looker, "_previousActiveLookable") as UnityEngine.Object)} " +
                                $"inRadius={n} [{names}] failed={F(looker, "_lastFailedConstraintType")} " +
                                $"canSee={Count(F(looker, "_canSeeConstraints"))} logs={F(looker, "_enableLogs")}");
                }
                var pres = All<InteractionSystem.Implementation.UI.DistantInteractionUIPresenter>();
                for (int i = 0; i < pres.Length; i++)
                {
                    var p = pres[i];
                    if (p == null || !InScene(p.gameObject)) continue;
                    var av = F(p, "_activeView") as Component;
                    if (av == null) continue;
                    var owner = F(av, "_owner") as UnityEngine.Object;
                    var shown = F(av, "IsShown");
                    log.LogInfo($"[reel:act] state interactor current={Descr(F(F(looker, "_interactor"), "_currentInteractable") as UnityEngine.Object)}");
                    log.LogInfo($"[reel:act] state presenter activeView shown={shown} owner={Descr(owner)} " +
                                $"button='{(F(av, "_interactButtonTMP") as TMPro.TMP_Text)?.text}' " +
                                $"name='{(F(av, "_interactionNameTMP") as TMPro.TMP_Text)?.text}' " +
                                $"cooldown='{(F(av, "_cooldownTimerTMP") as TMPro.TMP_Text)?.text}'");
                    break;
                }
                var p2 = F(_ui, "_prompt") as Component;
                if (p2 != null)
                    log.LogInfo($"[reel:act] state prompt action={Loc(F(p2, "_currentActionName"))} " +
                                $"cached='{F(p2, "_cachedActionName")}' canShow={F(p2, "CanShow")}");
            }
        }
        catch (Exception e) { log.LogWarning($"[reel:act] state failed: {e.Message}"); }
    }

    private static InteractionSystem.Implementation.Lookers.DistantInteractableLooker LocalLooker()
    {
        var lookers = All<InteractionSystem.Implementation.Lookers.DistantInteractableLooker>();
        for (int i = 0; i < lookers.Length; i++)
        {
            var l = lookers[i];
            if (l == null || !InScene(l.gameObject)) continue;
            if (F(l, "IsOwner") is bool b && b) return l;
        }
        return null;
    }

    // The native input path: a synthetic key event through the Input System, so the game's own
    // InteractableInputHandler performs the interaction (nothing here calls Interact).
    internal void Press(IHostContext log, string keyName)
    {
        try
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) { log?.LogWarning("[reel:act] press: no Keyboard device"); return; }
            UnityEngine.InputSystem.Key key;
            if (!Enum.TryParse(keyName, true, out key)) { log?.LogWarning($"[reel:act] press: '{keyName}' is not a Key"); return; }
            _pressKey = key;
            _pressAt = Time.unscaledTime;
            _pressPhase = 1;
            _pressAcceptedBefore = _accepted;
            // Queue only: calling InputSystem.Update() from a MonoBehaviour update is re-entrant and
            // is ignored, which is why an injected press never reached the game's action. The game's
            // own input update processes this event on its next frame.
            UnityEngine.InputSystem.InputSystem.QueueStateEvent<UnityEngine.InputSystem.LowLevel.KeyboardState>(
                kb, new UnityEngine.InputSystem.LowLevel.KeyboardState(key));
            bool down = false;
            try { down = kb[key].isPressed; } catch { }
            log?.LogInfo($"[reel:act] press key={key} queued focused={Application.isFocused} kb={kb != null} " +
                         $"pressed={down} background={UnityEngine.InputSystem.InputSystem.settings.backgroundBehavior} " +
                         $"mode={UnityEngine.InputSystem.InputSystem.settings.updateMode}");
        }
        catch (Exception e) { log?.LogWarning($"[reel:act] press failed: {e.Message}"); }
    }

    private UnityEngine.InputSystem.Key _pressKey;
    private float _pressAt;
    private int _pressPhase;
    private bool _pressSaw;
    private int _pressAcceptedBefore;

    internal void PressTick(IHostContext log)
    {
        try
        {
            if (_pressPhase == 1)
            {
                var k = UnityEngine.InputSystem.Keyboard.current;
                if (k != null && (k[_pressKey].wasPressedThisFrame || k[_pressKey].isPressed)) _pressSaw = true;
                if (Time.unscaledTime - _pressAt < 0.12f) return;
                _pressPhase = 2;
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null)
                    UnityEngine.InputSystem.InputSystem.QueueStateEvent<UnityEngine.InputSystem.LowLevel.KeyboardState>(
                        kb, new UnityEngine.InputSystem.LowLevel.KeyboardState());
                log?.LogInfo($"[reel:act] press released (device saw press={_pressSaw}) accepted={_accepted}");
                return;
            }
            // A queued device event is not delivered to the game's own action from inside a
            // MonoBehaviour update (InputSystem.Update() there is re-entrant and ignored). If the
            // key produced no interaction, run the handler's own bound callback instead, so the
            // unattended request path still goes through InteractableInputHandler.
            if (_pressPhase == 2 && Time.unscaledTime - _pressAt >= 0.45f)
            {
                _pressPhase = 3;
                if (_accepted == _pressAcceptedBefore)
                {
                    log?.LogInfo("[reel:act] press: no device event delivered, invoking the handler callback (via=handler)");
                    HandlerFire(log);
                }
                else log?.LogInfo($"[reel:act] press: native key path fired accepted={_accepted} (via=device)");
            }
        }
        catch (Exception e) { _pressPhase = 3; log?.LogWarning($"[reel:act] press tick failed: {e.Message}"); }
    }

    // Test hook: put the local player on the floor <dist> metres in front of this booth, facing it,
    // through the same PlayerTeleportator.TeleportOnServer the camera harness uses. A locked desktop
    // has no way to walk up to the reel, and the native pull only happens when the looker holds the
    // booth, so the unattended run needs to be able to arrive at it.
    private void StandPlayer(IHostContext log, string val)
    {
        try
        {
            var root = _rt != null ? _rt.Root : null;
            if (root == null) { log?.LogWarning("[reel:act] stand: no booth root"); return; }
            float dist = 1.6f, addYaw = 0f;
            var parts = (val ?? "").Split(',');
            if (parts.Length > 0) float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out dist);
            if (parts.Length > 1) float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out addYaw);
            Vector3 fwd = root.transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 target = root.transform.position + fwd * dist;
            target.y = _rt.Anchor.y;
            float yaw = root.transform.eulerAngles.y + 180f + addYaw;
            var rot = Quaternion.Euler(0f, yaw, 0f);
            var player = ReelLogic.FindLocalPlayerEntity();
            if (player == null) { log?.LogWarning("[reel:act] stand: no local player"); return; }
            var all = SceneCache.Scan(Il2CppType.Of<PlayerTeleportator>());
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i].TryCast<PlayerTeleportator>();
                if (t == null || t.transform == null) continue;
                if (!Under(t.transform, player.transform)) continue;
                t.TeleportOnServer(target, rot, true);
                log?.LogInfo($"[reel:act] stand dist={dist:F2} pos={V(target)} yaw={yaw:F1} " +
                             $"booth={V(root.transform.position)} via=PlayerTeleportator.TeleportOnServer (harness)");
                return;
            }
            player.transform.position = target;
            log?.LogInfo($"[reel:act] stand dist={dist:F2} pos={V(target)} yaw={yaw:F1} via=player.transform (harness)");
        }
        catch (Exception e) { log?.LogWarning($"[reel:act] stand failed: {e.Message}"); }
    }

    // Absolute harness form: standat=x,y,z,yaw.
    private void StandPlayerAt(IHostContext log, string val)
    {
        try
        {
            var parts = (val ?? "").Split(',');
            if (parts.Length < 4) { log?.LogWarning("[reel:act] standat: expected x,y,z,yaw"); return; }
            float x = 0f, y = 0f, z = 0f, yaw = 0f;
            float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x);
            float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y);
            float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out z);
            float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out yaw);
            var target = new Vector3(x, y, z);
            var rot = Quaternion.Euler(0f, yaw, 0f);
            var player = ReelLogic.FindLocalPlayerEntity();
            if (player == null) { log?.LogWarning("[reel:act] standat: no local player"); return; }
            var all = SceneCache.Scan(Il2CppType.Of<PlayerTeleportator>());
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i].TryCast<PlayerTeleportator>();
                if (t == null || t.transform == null) continue;
                if (!Under(t.transform, player.transform)) continue;
                t.TeleportOnServer(target, rot, true);
                log?.LogInfo($"[reel:act] standat pos={V(target)} yaw={yaw:F1} via=PlayerTeleportator.TeleportOnServer (harness)");
                return;
            }
            player.transform.position = target;
            log?.LogInfo($"[reel:act] standat pos={V(target)} yaw={yaw:F1} via=player.transform (harness)");
        }
        catch (Exception e) { log?.LogWarning($"[reel:act] standat failed: {e.Message}"); }
    }

    private void SetLookerLogs(IHostContext log, bool on)
    {
        var l = LocalLooker();
        if (l == null) { log?.LogWarning("[reel:act] no local looker"); return; }
        SetF(l, "_enableLogs", on);
        log?.LogInfo($"[reel:act] looker _enableLogs={on}");
    }

    // One request line for a booth addressed as '@<n>' by the primary booth's poll.
    internal void HandleRequestLine(IHostContext log, string line)
    {
        _log = log;
        try
        {
            if (line.Equals("state", StringComparison.OrdinalIgnoreCase)) DumpState(log, _rt);
            else if (line.StartsWith("press=", StringComparison.OrdinalIgnoreCase)) Press(log, line.Substring(6).Trim());
            else if (line.StartsWith("standat=", StringComparison.OrdinalIgnoreCase)) StandPlayerAt(log, line.Substring(8).Trim());
            else if (line.StartsWith("stand=", StringComparison.OrdinalIgnoreCase)) StandPlayer(log, line.Substring(6).Trim());
            else if (line.Equals("why", StringComparison.OrdinalIgnoreCase)) Why(log);
            else if (line.Equals("frames=", StringComparison.OrdinalIgnoreCase)) StartFrameLog(log, "6");
            else if (line.Equals("handlerfire", StringComparison.OrdinalIgnoreCase)) HandlerFire(log);
            else log.LogWarning($"[reel:act] booth {Live.IndexOf(this) + 1}: '{line}' is not a per-booth command");
        }
        catch (Exception e) { log?.LogWarning($"[reel:act] booth line '{line}' failed: {e.Message}"); }
    }

    // Every live booth in one place: each must have its own collider, look centre, provider and
    // InteractedLocally subscription. Requests only prove the addressed one, so this is the census.
    internal static void DumpBooths(IHostContext log)
    {
        log.LogInfo($"[reel:act] booths live={Live.Count}");
        for (int i = 0; i < Live.Count; i++)
        {
            var b = Live[i];
            if (b == null) { log.LogInfo($"[reel:act] booth[{i + 1}] null"); continue; }
            try
            {
                var box = F(b._lookable, "_collider") as Collider;
                var centre = F(b._lookable, "_lookableCenter") as Transform;
                log.LogInfo($"[reel:act] booth[{i + 1}] built={b._built} accepted={b._accepted} " +
                            $"go='{PathOf(b._go.transform)}' layer={b._go.layer} " +
                            $"boothRoot='{PathOf(b._rt?.Root.transform)}' " +
                            $"collider={(box != null ? V(box.bounds.center) + "/" + V(box.bounds.size) : "-")} " +
                            $"lookCentre={(centre != null ? V(centre.position) : "-")} " +
                            $"uiState={F(b._ui, "_uiState")} " +
                            $"provider='{PathOf((F(b._lookable, "_interactionProvider") as Component)?.transform)}' " +
                            $"key='{F(F(F(b._lookable, "_interactionProvider"), "Interaction"), "InteractionKeyName")}' " +
                            $"spawned={F(b._interactable, "IsSpawned")} subscribed={b._subscribed} state='{b._state}'");
            }
            catch (Exception e) { log.LogInfo($"[reel:act] booth[{i + 1}] dump failed: {e.Message}"); }
        }
    }

    // Why is the looker not holding it: run the interactable's own CanInteract with the player's
    // real interactor and name the constraint that refuses, plus the lookable's own constraints.
    internal void Why(IHostContext log)
    {
        try
        {
            var player = ReelLogic.FindLocalPlayerEntity();
            InteractionSystem.Implementation.Interactors.PlayerInteractor pi = null;
            if (player != null)
            {
                var arr = player.GetComponentsInChildren<InteractionSystem.Implementation.Interactors.PlayerInteractor>(true);
                if (arr.Length > 0) pi = arr[0];
            }
            log.LogInfo($"[reel:act] why player={(player != null ? player.name : "null")} interactor={(pi != null ? PathOf(pi.transform) : "null")}");
            if (_interactable == null) { log.LogWarning("[reel:act] why: not built"); return; }
            try
            {
                var m = _interactable.GetType().GetMethod("CanInteract",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(InteractionSystem.Core.Interactor), typeof(InteractionSystem.Core.InteractionConstraint).MakeByRefType() }, null);
                if (m == null)
                    log.LogWarning("[reel:act] why: CanInteract(Interactor, out InteractionConstraint) not found on " + TypeName(_interactable));
                else
                {
                    var args = new object[] { pi, null };
                    var ok = m.Invoke(_interactable, args);
                    var c = args[1];
                    log.LogInfo($"[reel:act] why CanInteract={(ok as bool?)?.ToString()} constraint={Descr(c as UnityEngine.Object)}({TypeName(c)})");
                    DumpFields(log, "why constraint", c, 12);
                }
            }
            catch (TargetInvocationException e)
            {
                log.LogWarning($"[reel:act] why CanInteract threw: {(e.InnerException ?? e).GetType().Name}: {(e.InnerException ?? e).Message}");
            }
            // The looker's own verdicts, called directly so "not in radius" and "cannot look" are
            // distinguishable without guessing.
            var looker = LocalLooker();
            if (looker != null && _lookable != null)
            {
                var it = F(looker, "_interactor") as Component;
                var origin = it != null ? (F(it, "_lookOrigin") as Transform) : null;
                var centre = F(_lookable, "_lookableCenter") as Transform;
                if (origin != null && centre != null)
                {
                    Vector3 to = centre.position - origin.position;
                    float flat = to.magnitude;
                    log.LogInfo($"[reel:act] why lookOrigin='{PathOf(origin)}' pos={V(origin.position)} fwd={V(origin.forward)} " +
                                $"toCentre={V(to)} dist={flat:F2} dot={Vector3.Dot(origin.forward, to / Mathf.Max(0.0001f, flat)):F3}");
                }
                foreach (var m in new[] { "CanLook", "GetLookable", "IsAtLeastOneLookableConstraintCantSee" })
                {
                    try
                    {
                        var mi = looker.GetType().GetMethod(m, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (mi == null) { log.LogInfo($"[reel:act] why {m}: not found"); continue; }
                        var ps = mi.GetParameters();
                        var args2 = ps.Length > 0 ? new object[] { _lookable } : null;
                        var r = mi.Invoke(looker, args2);
                        log.LogInfo($"[reel:act] why {m}({ps.Length}) = {(r is UnityEngine.Object uo ? Descr(uo) : (r?.ToString() ?? "null"))}");
                    }
                    catch (TargetInvocationException e)
                    {
                        var ie = e.InnerException ?? e;
                        log.LogWarning($"[reel:act] why {m} threw {ie.GetType().Name}: {ie.Message}");
                    }
                    catch (Exception e) { log.LogWarning($"[reel:act] why {m} failed: {e.Message}"); }
                }
            }
            var lk = F(_lookable, "_lookableConstraints");
            int n = Count(lk);
            log.LogInfo($"[reel:act] why lookableConstraints={n}");
            for (int i = 0; i < n && i < 8; i++)
            {
                var o = At(lk, i) as UnityEngine.Object;
                log.LogInfo($"[reel:act] why lookableConstraint[{i}] {Descr(o)} comps=[{Short(CompTypes(((Component)o).gameObject))}]");
                DumpFields(log, $"why lookableConstraint[{i}]", o, 10);
            }
            // Which of the lookable's can-see converters refuses: each wraps one interactable
            // constraint, so the source type names the rule that is actually blocking.
            for (int i = 0; i < n && i < 8; i++)
            {
                var conv = At(lk, i);
                if (conv == null) continue;
                var src = F(conv, "_interactionConstraintToConvert");
                string verdict = "?";
                try
                {
                    var m = conv.GetType().GetMethod("CanSee", BindingFlags.Public | BindingFlags.Instance);
                    if (m != null) verdict = (m.Invoke(conv, new object[] { _lookable, looker }) as bool?)?.ToString() ?? "null";
                }
                catch (TargetInvocationException e) { verdict = "threw " + (e.InnerException ?? e).GetType().Name; }
                catch (Exception e) { verdict = "threw " + e.GetType().Name; }
                log.LogInfo($"[reel:act] why converter[{i}] source={TypeName(src)} '{((src as Component)?.transform != null ? PathOf(((Component)src).transform) : "-")}' " +
                            $"alive={(src is UnityEngine.Object uo2 && uo2 != null)} CanSee={verdict}");
            }

            var ia = F(_interactable, "Constraints");
            log.LogInfo($"[reel:act] why interactable Constraints={Descr(ia as UnityEngine.Object)} ({TypeName(ia)}) count={Count(ia)}");
            if (_interactable != null) DumpHierarchy(log, "why interactable", _interactable.transform, 4);
        }
        catch (Exception e) { log.LogWarning($"[reel:act] why failed: {e.Message}"); }
    }

    // ---- probe ---------------------------------------------------------------------------

    internal void Probe(IHostContext log, LobbyPropRuntime rt)
    {
        log.LogInfo("[reel:act] ===== probe begin =====");
        try { ProbeNet(log); } catch (Exception e) { log.LogWarning($"[reel:act] net probe: {e.Message}"); }
        try { ProbeLayers(log); } catch (Exception e) { log.LogWarning($"[reel:act] layer probe: {e.Message}"); }
        try { ProbeLookables(log); } catch (Exception e) { log.LogWarning($"[reel:act] lookables: {e.Message}"); }
        try { ProbeLookers(log); } catch (Exception e) { log.LogWarning($"[reel:act] lookers: {e.Message}"); }
        try { ProbePresenter(log); } catch (Exception e) { log.LogWarning($"[reel:act] presenter: {e.Message}"); }
        try { ProbeHandlers(log); } catch (Exception e) { log.LogWarning($"[reel:act] handlers: {e.Message}"); }
        try { ProbeEmpty(log); } catch (Exception e) { log.LogWarning($"[reel:act] empty: {e.Message}"); }
        try { PropInfo(log, rt); } catch (Exception e) { log.LogWarning($"[reel:act] prop: {e.Message}"); }
        log.LogInfo($"[reel:act] ===== probe end (built={_built} '{_state}') =====");
    }

    private static void ProbeNet(IHostContext log)
    {
        var nm = Unity.Netcode.NetworkManager.Singleton;
        if (nm == null) { log.LogInfo("[reel:act] net NetworkManager.Singleton=null"); return; }
        log.LogInfo($"[reel:act] net nm='{PathOf(nm.transform)}' active={nm.gameObject.activeInHierarchy} " +
                    $"IsHost={nm.IsHost} IsServer={nm.IsServer} IsClient={nm.IsClient} IsListening={nm.IsListening} " +
                    $"LocalClientId={nm.LocalClientId}");
        log.LogInfo($"[reel:act] net AddNetworkPrefab={(typeof(Unity.Netcode.NetworkManager).GetMethod("AddNetworkPrefab") != null ? "yes" : "no")} " +
                    $"InstantiateAndSpawn={(typeof(Unity.Netcode.NetworkManager).GetMethod("InstantiateAndSpawn") != null ? "yes" : "no")} " +
                    $"GetNetworkObject={(typeof(Unity.Netcode.NetworkManager).GetMethod("GetNetworkObject") != null ? "yes" : "no")}");
        try
        {
            var cfg = nm.NetworkConfig;
            object prefs = F(cfg, "Prefabs");
            object list = F(prefs, "m_Prefabs") ?? F(prefs, "Prefabs");
            int n = Count(list);
            log.LogInfo($"[reel:act] net prefabs={n} (Prefabs={TypeName(prefs)})");
            if (n <= 0) return;
            for (int i = 0; i < n; i++)
            {
                object item = At(list, i);
                if (item == null) continue;
                var go = F(item, "Prefab") as GameObject;
                if (go == null) continue;
                string comps = CompTypes(go);
                string name = go.name;
                bool interesting = name.IndexOf("interact", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("empty", StringComparison.OrdinalIgnoreCase) >= 0
                    || comps.IndexOf("EmptyInteractable", StringComparison.Ordinal) >= 0
                    || comps.IndexOf("LookableDistantInteractable", StringComparison.Ordinal) >= 0;
                log.LogInfo($"[reel:act] net prefab[{i}] '{name}' {(interesting ? "*** " : "")}comps=[{Short(comps)}]");
            }
        }
        catch (Exception e) { log.LogWarning($"[reel:act] net prefabs: {e.Message}"); }
    }

    private static void ProbeLayers(IHostContext log)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 32; i++)
        {
            string n = LayerMask.LayerToName(i);
            if (string.IsNullOrEmpty(n)) continue;
            sb.Append(i).Append('=').Append(n).Append(' ');
        }
        log.LogInfo($"[reel:act] layers {sb}");
    }

    private static void ProbeLookables(IHostContext log)
    {
        var arr = All<InteractionSystem.Implementation.Lookables.LookableDistantInteractable>();
        int inScene = 0, shown = 0;
        var names = new StringBuilder();
        for (int i = 0; i < arr.Length; i++)
        {
            var l = arr[i];
            if (l == null) continue;
            GameObject go = null;
            try { go = l.gameObject; } catch { }
            if (!InScene(go)) continue;
            inScene++;
            if (names.Length < 600) names.Append(l.name).Append(' ');
            if (shown >= 8) continue;
            int k = shown++;
            try
            {
                log.LogInfo($"[reel:act] lookable[{k}] '{PathOf(l.transform)}' active={go.activeInHierarchy} " +
                            $"layer={go.layer}({LayerMask.LayerToName(go.layer)}) enabled={l.enabled} " +
                            $"collider={Descr(F(l, "_collider") as UnityEngine.Object)} " +
                            $"center={Descr(F(l, "_lookableCenter") as UnityEngine.Object)} " +
                            $"ui={Descr(F(l, "_ui") as UnityEngine.Object)} " +
                            $"overrideDot={F(l, "_overrideRequireFacingDot")} dot={F(l, "_overridenRequiredFacingDot")} " +
                            $"comps=[{Short(CompTypes(go))}]");
                var ia = F(l, "_interactable");
                if (ia is UnityEngine.Object)
                    log.LogInfo($"[reel:act] lookable[{k}].interactable type={TypeName(ia)} " +
                                $"spawned={F(ia, "IsSpawned")} netObj={Descr(F(ia, "NetworkObject") as UnityEngine.Object)} " +
                                $"waiting={F(ia, "IsWaitingRequestFromServer")} " +
                                $"localEvent={(F(ia, "InteractedLocally") == null ? "null" : "set")} " +
                                $"comps=[{Short(CompTypes(((Component)ia).gameObject))}]");
                var prov = F(l, "_interactionProvider");
                if (prov != null)
                    log.LogInfo($"[reel:act] lookable[{k}].provider type={TypeName(prov)} " +
                                $"interaction={Descr(F(prov, "Interaction") as UnityEngine.Object)} " +
                                $"keyName='{F(F(prov, "Interaction"), "InteractionKeyName")}'");
                DumpFields(log, $"lookable[{k}]", l, 16);
            }
            catch (Exception e) { log.LogWarning($"[reel:act] lookable[{k}] dump: {e.Message}"); }
        }
        log.LogInfo($"[reel:act] lookables={arr.Length} inScene={inScene} names=[{names}]");
    }

    private static void ProbeLookers(IHostContext log)
    {
        var lookers = All<InteractionSystem.Implementation.Lookers.DistantInteractableLooker>();
        int shown = 0;
        for (int i = 0; i < lookers.Length; i++)
        {
            var l = lookers[i];
            if (l == null || !InScene(l.gameObject)) continue;
            try
            {
                log.LogInfo($"[reel:act] looker[{shown}] '{PathOf(l.transform)}' active={l.gameObject.activeInHierarchy} " +
                            $"owner={F(l, "IsOwner")} radius={F(l, "_maxLookableCheckRadius")} dot={F(l, "_requiredFacingDot")} " +
                            $"interactor={Descr(F(l, "_interactor") as UnityEngine.Object)} " +
                            $"currentActive={Descr(F(l, "_currentActiveLookable") as UnityEngine.Object)} " +
                            $"currentFrame={Descr(F(l, "_currentFrameLookable") as UnityEngine.Object)} " +
                            $"radiusList={Count(F(l, "_lookablesInRadiusCurrentUpdate"))}");
            }
            catch (Exception e) { log.LogWarning($"[reel:act] looker[{shown}]: {e.Message}"); }
            shown++;
        }
        log.LogInfo($"[reel:act] lookers={lookers.Length} inScene={shown}");
        var inter = All<InteractionSystem.Core.Interactor>();
        int ishown = 0;
        for (int i = 0; i < inter.Length && ishown < 4; i++)
        {
            var it = inter[i];
            if (it == null || !InScene(it.gameObject)) continue;
            try
            {
                log.LogInfo($"[reel:act] interactor[{ishown}] '{PathOf(it.transform)}' type={TypeName(it)} " +
                            $"current={Descr(F(it, "_currentInteractable") as UnityEngine.Object)} " +
                            $"origin={Descr(F(it, "_lookOrigin") as UnityEngine.Object)} " +
                            $"comps=[{Short(CompTypes(it.gameObject))}]");
            }
            catch (Exception e) { log.LogWarning($"[reel:act] interactor[{ishown}]: {e.Message}"); }
            ishown++;
        }
    }

    private static void ProbePresenter(IHostContext log)
    {
        var pres = All<InteractionSystem.Implementation.UI.DistantInteractionUIPresenter>();
        int shown = 0;
        for (int i = 0; i < pres.Length; i++)
        {
            var p = pres[i];
            if (p == null || !InScene(p.gameObject)) continue;
            try
            {
                log.LogInfo($"[reel:act] presenter[{shown}] '{PathOf(p.transform)}' active={p.gameObject.activeInHierarchy} " +
                            $"Instance={Descr(F(p, "Instance") as UnityEngine.Object)} " +
                            $"activeView={Descr(F(p, "_activeView") as UnityEngine.Object)} " +
                            $"errorView={Descr(F(p, "_errorView") as UnityEngine.Object)} " +
                            $"hintView={Descr(F(p, "_hintView") as UnityEngine.Object)} " +
                            $"attractionPrefab={Descr(F(p, "_attractionPrefab") as UnityEngine.Object)} " +
                            $"overlayLayer={F(p, "_overlayLayer")} cam={Descr(F(p, "_cameraTransform") as UnityEngine.Object)} " +
                            $"pool={Count(F(p, "_pool"))} comps=[{Short(CompTypes(p.gameObject))}]");
                var av = F(p, "_activeView") as Component;
                if (av != null) DumpView(log, "activeView", av);
                var ev = F(p, "_errorView") as Component;
                if (ev != null) DumpView(log, "errorView", ev);
                if (av != null) DumpHierarchy(log, "activeView", av.transform, 2);
            }
            catch (Exception e) { log.LogWarning($"[reel:act] presenter[{shown}]: {e.Message}"); }
            shown++;
        }
        log.LogInfo($"[reel:act] presenters={pres.Length} inScene={shown}");

        var uis = All<InteractionSystem.Implementation.UI.DistantInteractionWorldSpaceUI>();
        int ushown = 0;
        for (int i = 0; i < uis.Length && ushown < 6; i++)
        {
            var u = uis[i];
            if (u == null || !InScene(u.gameObject)) continue;
            try
            {
                log.LogInfo($"[reel:act] wsUI[{ushown}] '{PathOf(u.transform)}' active={u.gameObject.activeInHierarchy} " +
                            $"state={F(u, "_uiState")} prompt={Descr(F(u, "_prompt") as UnityEngine.Object)} " +
                            $"anchor={Descr(F(u, "_anchor") as UnityEngine.Object)} " +
                            $"comps=[{Short(CompTypes(u.gameObject))}]");
                var pr = F(u, "_prompt") as Component;
                if (pr != null)
                    log.LogInfo($"[reel:act] wsUI[{ushown}].prompt type={TypeName(pr)} " +
                                $"lookable={Descr(F(pr, "_lookableDistanceInteractable") as UnityEngine.Object)} " +
                                $"action={Loc(F(pr, "_currentActionName"))} cached='{F(pr, "_cachedActionName")}' " +
                                $"comps=[{Short(CompTypes(pr.gameObject))}]");
            }
            catch (Exception e) { log.LogWarning($"[reel:act] wsUI[{ushown}]: {e.Message}"); }
            ushown++;
        }
        log.LogInfo($"[reel:act] worldSpaceUIs={uis.Length} inScene={ushown}");
    }

    private static void ProbeHandlers(IHostContext log)
    {
        var hs = All<InputSystem.Interaction.InteractableInputHandler>();
        int shown = 0;
        for (int i = 0; i < hs.Length; i++)
        {
            var h = hs[i];
            if (h == null || !InScene(h.gameObject)) continue;
            try
            {
                var pi = F(h, "_previousLookableInteraction");
                log.LogInfo($"[reel:act] handler[{shown}] '{PathOf(h.transform)}' active={h.gameObject.activeInHierarchy} " +
                            $"interactor={Descr(F(h, "_interactor") as UnityEngine.Object)} " +
                            $"looker={Descr(F(h, "_interactableLooker") as UnityEngine.Object)} " +
                            $"lookerGo={Descr(F(h, "_interactableLookerGameObject") as UnityEngine.Object)} " +
                            $"previousInteraction={Descr(pi as UnityEngine.Object)} keyName='{F(pi, "InteractionKeyName")}' " +
                            $"previousLookable={Descr(F(h, "_previousLookableInteractable") as UnityEngine.Object)} " +
                            $"comps=[{Short(CompTypes(h.gameObject))}]");
            }
            catch (Exception e) { log.LogWarning($"[reel:act] handler[{shown}]: {e.Message}"); }
            shown++;
        }
        log.LogInfo($"[reel:act] inputHandlers={hs.Length} inScene={shown}");
    }

    private static void ProbeEmpty(IHostContext log)
    {
        var es = All<InteractionSystem.Implementation.Interactables.EmptyInteractable>();
        int shown = 0;
        for (int i = 0; i < es.Length; i++)
        {
            var e = es[i];
            if (e == null || !InScene(e.gameObject)) continue;
            try
            {
                log.LogInfo($"[reel:act] empty[{shown}] '{PathOf(e.transform)}' active={e.gameObject.activeInHierarchy} " +
                            $"layer={e.gameObject.layer}({LayerMask.LayerToName(e.gameObject.layer)}) " +
                            $"spawned={e.IsSpawned} netObj={Descr(e.NetworkObject as UnityEngine.Object)} " +
                            $"comps=[{Short(CompTypes(e.gameObject))}]");
            }
            catch (Exception ex) { log.LogWarning($"[reel:act] empty[{shown}]: {ex.Message}"); }
            shown++;
        }
        log.LogInfo($"[reel:act] emptyInteractables={es.Length} inScene={shown}");

        var ia = All<InteractionSystem.Core.Interactable>();
        int ishown = 0;
        var kinds = new StringBuilder();
        for (int i = 0; i < ia.Length && ishown < 6; i++)
        {
            var it = ia[i];
            if (it == null || !InScene(it.gameObject)) continue;
            try
            {
                kinds.Append(TypeName(it)).Append("@'").Append(it.name).Append("' ");
                log.LogInfo($"[reel:act] interactable[{ishown}] '{PathOf(it.transform)}' type={TypeName(it)} " +
                            $"spawned={it.IsSpawned} netObj={Descr(it.NetworkObject as UnityEngine.Object)} " +
                            $"comps=[{Short(CompTypes(it.gameObject))}]");
                if (ishown < 2) DumpHierarchy(log, $"interactable[{ishown}]", it.transform, 4);
            }
            catch (Exception ex) { log.LogWarning($"[reel:act] interactable[{ishown}]: {ex.Message}"); }
            ishown++;
        }
        log.LogInfo($"[reel:act] interactables={ia.Length} first=[{kinds}]");

        // Prefab assets: loaded but not in a scene. This is how a reusable interactable prefab is
        // found without touching a live one.
        int assets = 0, ahits = 0;
        try
        {
            var gos = All<GameObject>();
            for (int i = 0; i < gos.Length && ahits < 40; i++)
            {
                var go = gos[i];
                if (go == null || InScene(go)) continue;
                assets++;
                string nm = go.name ?? "";
                if (nm.IndexOf("EmptyInteractable", StringComparison.OrdinalIgnoreCase) < 0 &&
                    nm.IndexOf("Interactable", StringComparison.OrdinalIgnoreCase) < 0) continue;
                ahits++;
                try
                {
                    log.LogInfo($"[reel:act] asset '{nm}' path='{PathOf(go.transform)}' layer={go.layer} " +
                                $"active={go.activeSelf} comps=[{Short(CompTypes(go))}] " +
                                $"children={go.transform.childCount}");
                }
                catch { }
            }
        }
        catch (Exception ex) { log.LogWarning($"[reel:act] asset scan: {ex.Message}"); }
        log.LogInfo($"[reel:act] assets scanned={assets} interactableish={ahits}");
    }

    private static void PropInfo(IHostContext log, LobbyPropRuntime rt)
    {
        var root = rt.Root;
        log.LogInfo($"[reel:act] prop root='{PathOf(root.transform)}' layer={root.layer}({LayerMask.LayerToName(root.layer)}) " +
                    $"children={root.transform.childCount} anchor={rt.Anchor} yaw={rt.Yaw:F1} " +
                    $"colliders={root.GetComponentsInChildren(Il2CppType.Of<Collider>()).Length} " +
                    $"comps=[{Short(CompTypes(root))}]");
        log.LogInfo($"[reel:act] prop audience: looking={rt.PlayerLookingAtBooth} hud='{ReelHud.Path}'");
    }

    private void Hierarchy(IHostContext log, LobbyPropRuntime rt)
    {
        if (rt?.Root == null) return;
        DumpHierarchy(log, "prop", rt.Root.transform, 3);
    }

    // ---- helpers -------------------------------------------------------------------------

    private static void DumpView(IHostContext log, string tag, UnityEngine.Object view)
    {
        try
        {
            log.LogInfo($"[reel:act] {tag} type={TypeName(view)} path='{PathOf(((Component)view).transform)}' " +
                        $"shown={F(view, "IsShown")} active={((Component)view).gameObject.activeInHierarchy} " +
                        $"bmpText='{(F(view, "_interactButtonTMP") as TMPro.TMP_Text)?.text}' " +
                        $"nameText='{(F(view, "_interactionNameTMP") as TMPro.TMP_Text)?.text}' " +
                        $"cooldown='{(F(view, "_cooldownTimerTMP") as TMPro.TMP_Text)?.text}' " +
                        $"warning='{(F(view, "_warningTMP") as TMPro.TMP_Text)?.text}' " +
                        $"comps=[{Short(CompTypes(((Component)view).gameObject))}]");
        }
        catch (Exception e) { log.LogWarning($"[reel:act] {tag}: {e.Message}"); }
    }

    private static void DumpFields(IHostContext log, string tag, object o, int max)
    {
        try
        {
            var sb = new StringBuilder();
            int n = 0;
            for (var t = o.GetType(); t != null && t != typeof(object) && n < max; t = t.BaseType)
            {
                foreach (var pi in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (pi.GetIndexParameters().Length != 0) continue;
                    string nm = pi.Name;
                    if (!nm.StartsWith("_", StringComparison.Ordinal)) continue;
                    string v;
                    try { v = Descr(pi.GetValue(o) as UnityEngine.Object) + "(" + TypeName(pi.GetValue(o)) + ")"; }
                    catch { continue; }
                    if (v.StartsWith("null(", StringComparison.Ordinal)) continue;
                    sb.Append(nm).Append('=').Append(v).Append(' ');
                    if (++n >= max) break;
                }
            }
            if (sb.Length > 0) log.LogInfo($"[reel:act] {tag} fields: {sb}");
        }
        catch { }
    }

    private static void DumpHierarchy(IHostContext log, string tag, Transform t, int depth)
    {
        Walk(log, tag, t, 0, depth);
    }

    private static void Walk(IHostContext log, string tag, Transform t, int level, int depth)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < level; i++) sb.Append("  ");
        log.LogInfo($"[reel:act] {tag} {sb}'{t.name}' layer={t.gameObject.layer} active={t.gameObject.activeSelf} " +
                    $"comps=[{Short(CompTypes(t.gameObject))}]");
        if (level >= depth) return;
        for (int i = 0; i < t.childCount; i++) Walk(log, tag, t.GetChild(i), level + 1, depth);
    }

    private static string CompTypes(GameObject go)
    {
        try
        {
            var cs = go.GetComponents<Component>();
            var sb = new StringBuilder();
            for (int i = 0; i < cs.Length; i++)
            {
                var c = cs[i];
                if (c == null) { sb.Append("null;"); continue; }
                sb.Append(TypeName(c)).Append(';');
            }
            return sb.ToString();
        }
        catch { return "?"; }
    }

    private static string Short(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("UnityEngine.", "").Replace("InteractionSystem.", "").Replace("Implementation.", "")
                .Replace(".", "/").TrimEnd(';');
    }

    private static string TypeName(object o)
    {
        if (o == null) return "null";
        try
        {
            var il2 = o as Il2CppObjectBase;
            if (il2 != null && il2.Pointer != IntPtr.Zero)
            {
                IntPtr cls = IL2CPP.il2cpp_object_get_class(il2.Pointer);
                if (cls != IntPtr.Zero)
                {
                    // il2cpp_class_get_name/namespace return a native const char*, NOT an Il2CppString.
                    string ns = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(cls));
                    string nm = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(cls));
                    if (!string.IsNullOrEmpty(nm)) return string.IsNullOrEmpty(ns) ? nm : ns + "." + nm;
                }
            }
            return o.GetType().FullName;
        }
        catch { try { return o.GetType().Name; } catch { return "?"; } }
    }

    private static string Descr(UnityEngine.Object o)
    {
        if (o == null) return "null";
        try
        {
            var c = o as Component;
            string p = c != null ? PathOf(c.transform) : o.name;
            string extra = "";
            if (o is Collider) { var col = (Collider)o; extra = $" enabled={col.enabled} trigger={col.isTrigger} b={V(col.bounds.center)}/{V(col.bounds.size)}"; }
            return $"{TypeName(o)}:'{p}'{extra}";
        }
        catch { return TypeName(o); }
    }

    internal static T[] All<T>() where T : UnityEngine.Object
    {
        try
        {
            var raw = SceneCache.Scan(Il2CppType.Of<T>());
            var list = new List<T>(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                var v = raw[i].TryCast<T>();
                if (v != null) list.Add(v);
            }
            return list.ToArray();
        }
        catch { return new T[0]; }
    }

    internal static string V(Vector3 v) => $"({v.x:F2},{v.y:F2},{v.z:F2})";

    internal static string PathOf(Transform t)
    {
        try
        {
            if (t == null) return "null";
            string s = t.name;
            int guard = 0;
            for (var p = t.parent; p != null && guard++ < 8; p = p.parent) s = p.name + "/" + s;
            return s;
        }
        catch { return "?"; }
    }

    private static int Count(object o)
    {
        if (o == null) return -1;
        try
        {
            var t = o.GetType();
            var p = t.GetProperty("Count") ?? t.GetProperty("Length");
            if (p != null && p.GetIndexParameters().Length == 0) return Convert.ToInt32(p.GetValue(o));
            var f = t.GetField("Count") ?? t.GetField("Length");
            if (f != null) return Convert.ToInt32(f.GetValue(o));
            var m = t.GetMethod("get_Count") ?? t.GetMethod("get_Length");
            if (m != null && m.GetParameters().Length == 0) return Convert.ToInt32(m.Invoke(o, null));
            return -1;
        }
        catch { return -1; }
    }

    private static object At(object o, int i)
    {
        if (o == null) return null;
        try
        {
            var t = o.GetType();
            var p = t.GetProperty("Item");
            if (p != null) return p.GetValue(o, new object[] { i });
            var m = t.GetMethod("get_Item", new[] { typeof(int) });
            if (m != null) return m.Invoke(o, new object[] { i });
        }
        catch { }
        return null;
    }

    private static string Loc(object o)
    {
        if (o == null) return "null";
        try
        {
            string text = null;
            var m = o.GetType().GetMethod("GetLocalizedString", Type.EmptyTypes);
            if (m != null) { try { text = m.Invoke(o, null) as string; } catch { } }
            var sb = new StringBuilder();
            sb.Append("table=");
            foreach (var field in new[] { "m_TableReference", "m_TableEntryReference" })
            {
                var f = o.GetType().GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null) { sb.Append(field).Append("=?"); continue; }
                var v = f.GetValue(o);
                sb.Append(field).Append('{');
                if (v != null)
                {
                    foreach (var inner in v.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        try { sb.Append(inner.Name).Append('=').Append(inner.GetValue(v)).Append(' '); } catch { }
                    }
                }
                sb.Append("} ");
            }
            if (!string.IsNullOrEmpty(text)) sb.Append("resolved='").Append(text).Append("'");
            return sb.ToString();
        }
        catch (Exception e) { return "err:" + e.Message; }
    }

    private static bool InScene(GameObject go)
    {
        try { return go != null && !string.IsNullOrEmpty(go.scene.name); } catch { return false; }
    }

    internal static object F(object o, string name)
    {
        if (o == null) return null;
        try
        {
            for (var t = o.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                var pi = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (pi != null && pi.GetIndexParameters().Length == 0) return pi.GetValue(o);
                var fi = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (fi != null) return fi.GetValue(o);
            }
            // Undeclared base search (interop puts inherited properties on the declaring proxy type).
            var any = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (any != null && any.GetIndexParameters().Length == 0) return any.GetValue(o);
        }
        catch { }
        return null;
    }
}
