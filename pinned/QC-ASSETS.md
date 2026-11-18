# Quantum Console — dev vs pin, statically, and what the command table actually is

Pass of 2026-11-14, on the pinned build (buildid 26597574) and the `devbuild_unstable` candidate
(buildid 26642252, `C:\AlterumCandidates\devbuild_unstable`). Read-only on both installs; the
runtime work was done on `C:\AlterumDev` (hardlink copy of the pin). Tool: UnityPy 1.25.4,
`work/qc/devpin_qc.py` (raw MonoBehaviour header parse + MonoScript map + hand-parsed GameObject),
output `work/qc/devpin_qc.json`.

## Side-by-side: every `QFSW.QC.QuantumConsole` in both builds

`m_IsActive` is the owner GameObject's active flag, `m_Enabled` the component's, both read from the
serialized object (not from a running game).

| file | object | pin active/enabled | dev active/enabled | payload |
|---|---|---|---|---|
| `level3` | Quantum Console (SRP) Canvas | 1 / 1 | 1 / 1 | 340 B, identical modulo PPtr ids |
| `level4` | Quantum Console (SRP) Canvas | 1 / 1 | 1 / 1 | same |
| `level5` | Quantum Console (SRP) | **0** / 1 | **0** / 1 | same (menu scene: object inactive) |
| `level6` | Quantum Console (SRP) Canvas | 1 / 1 | 1 / 1 | same |
| `level8` | Quantum Console (SRP) Canvas | 1 / 1 | 1 / 1 | same |
| `level9` | Quantum Console (SRP) Canvas | 1 / 1 | 1 / 1 | same |
| `resources.assets` ×5 | Quantum Console (SRP) Canvas | 1 / 1 | 1 / 1 | same (prefab assets) |

Side-by-side counts over 22 asset files (globalgamemanagers.assets, resources.assets, level0-9,
sharedassets0-9): **QuantumConsole 11 / 11, ConsoleInstaller 11 / 11, ConsoleInputHandler 13 / 13**,
`DebugCanvas`/`DebugGUI`/`DebugFrameOverlay` unchanged.

Payload diff, same scene, pin vs dev (`level3`..`level9`): 3 bytes on the two lobby-identical scenes,
18 bytes on the shifted ones, **every one of them inside a PPtr** (`m_FileID`/`m_PathID` low bytes,
+3 shift from the objects the dev build added). `ConsoleInstaller`'s 12-byte payload (the single
`_console` reference) differs only in that same PPtr id. No bool, no enum, no settings value differs.

Component list of the console object, identical in both builds:
`RectTransform, QuantumConsole(), Canvas, GraphicRaycaster, CanvasScaler, BlurShaderController,
DynamicCanvasScaler`. No enabler/gate component of any kind on the object or on `ConsoleInstaller`
(`ConsoleInstaller` has exactly one component and one serialized field).

Settings read at runtime in the pin (unchanged from `QC-CONSOLE.md`): `_activateOnStartup=true`,
`_initialiseOnStartup=true`, `_supportedState=Always`, `_singletonMode=false` (menu) / `true`
(lobby), `ToggleConsoleVisibilityKey=Tilde`, `SubmitCommandKey=Return`. No dev-only settings asset
exists, and `QuantumConsoleProcessor` (a MonoBehaviour type in this QC version) has **no serialized
instance in either build** — it is created on demand.

## What that proves

1. **The dev build is identical here.** There is no serialized difference to replicate: not the
   activation mode, not the key, not the enabled flag, not an installer field. The dev route is dead
   as a source of an "on" switch.
2. **The pin's own asset data says the console is on**: in the lobby scenes the object is active and
   the component enabled *on disk*. The `enabled=false` observed at runtime is therefore set by game
   code after the scene loads — but it is not a serialized gate that can be flipped by editing data,
   and the dev build carries the same code path.
3. Since `m_Enabled=1` is what the game loads and the component still reads `false` at runtime, the
   earlier conclusion ("deliberately dormant in data") was wrong in its mechanism: something in
   `Assembly-CSharp` disables it, and the console has to be brought up through its own API.

## Runtime: what now works, what does not

Done in `C:\AlterumDev` with `-alterumInstance=qc` (private lobby via the config's `ClickSequence`),
driving the game's own code through `AlterumModelSwap.Hot/GameConsole.cs` (`qc.request`):

*Finding that unlocked the table.* The earlier probe invoked `Awake`/`Initialize` and reported
"nothing happened". The reason: `GetComponents<Component>()` returns proxies whose managed type is
`UnityEngine.Component` even when `GetIl2CppType()` reports `QFSW.QC.QuantumConsole`. A
`Component` proxy has **no** `Awake`, no `Initialize` and no `enabled`, so reflection looked for
those methods on `UnityEngine.Component` and found nothing (`Awake/0 not found on
UnityEngine.Component` in the log) while the reported `enabled=true` write went nowhere. Asking the
GameObject for a **typed** prooxy (`GetComponent(Il2CppType.From(typeof(QuantumConsole)))`) fixes it:

```
[q] after Awake():        enabled=False Instance=null
[q] after Initialize():   enabled=False Instance=null
[q] processor: LoadedCommandCount=109 TableGenerated=True
```

So **`TableIsGenerating=True` forever / `LoadedCommandCount=0` was not a property of the build** —
it was the console never having been initialized through a correctly typed reference. With that
fixed the game's own processor holds a full table (`pinned/qc-commands-live.txt`, 82 lines).

*What still does not work.* `enabled = true` on the console keeps reading `false` immediately after
the write, with `Instance` null, while a control write on the same GameObject
(`GraphicRaycaster.enabled`) toggles normally — so the interop path is fine and something reverts
this specific component (console init does not set `Instance` even though `Initialize()` runs and
generates the table). `Activate()` therefore never produces `IsActive`, `Instance` stays null and
the Tilde handler (which lives in the component's own `Update`) never runs. The console UI and its
key are **not** available.

*What the table contains that matters for cheats* (full list in `qc-commands-live.txt`):
`quest.complete-all`, `quest.complete-task`, `quest.complete-task-wait`, `quest.complete-subtask`,
`quest.uncomplete-subtask`, `goto.place`, `gamecycle.*` (start/stop/start-night/start-resulting,
`open-all-exits`, `bless-all-totems`), `handhelds.get`, `handhelds.to-inventory`,
`handhelds.print-items-indexes`, `po.spawn-player`, `po.spawn-lobbier`, `effects.impose`,
`health.set`, `kcc.noclip`, `map.generate-maze`, `sabotages.start`, `roles-distribution.*`.
There is **no** generic "give item" command; the closest are `handhelds.get` /
`handhelds.to-inventory` (handheld tools to inventory) and `effects.impose` / `po.spawn-*` for
world objects.

*Executing them without a console instance* goes through the processor's public static
`InvokeCommand(string)`: `stats.all` reached the command and failed inside the game with
`Could not invoke the command because no objects of type StatisticsCommandsWrapper could be found`
(i.e. that wrapper does not exist in a lobby), and `quest.complete-all`, `handhelds.to-inventory`,
`goto.place` all raise `No overload of '<cmd>' with 0 parameters could be found` — they are
parameterized. Command signatures are not obtainable from the public API in this build:
`GenerateCommandManual(string)` only logs to a console that never initializes, and
`GetAllCommands()` returns an Il2Cpp generic `IEnumerable<CommandData>` whose enumerator exposes no
`MoveNext`/`Current` to reflection.

`help`/`man` do not exist in this QC build as commands (`Command 'help' could not be found`); the QC
built-ins are registered by the console component itself, which never initializes.
