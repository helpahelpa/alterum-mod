# Quantum Console in the pin — activation findings

Investigated on a hardlink copy `C:\AlterumDev` made from `C:\AlterumPinned` (pin buildid
26597574). The pin itself was not modified. Instance was started with
`-alterumInstance=qc`; the mod's `ClickSequence` created a private lobby; the console was inspected
live through a small BepInEx probe (`QcProbe.dll`).

## Settings read from the loaded `QuantumConsole` components

| Setting | Menu | Lobby |
|---|---|---|
| Path | `UI/Quantum Console (SRP)` | `Lobby Global/User Interface/Canvases/Quantum Console (SRP) Canvas` |
| GameObject activeSelf | **false** | true |
| Component `enabled` | true (but object inactive) | **false** |
| `QuantumConsole.Instance` | null | null |
| `_activateOnStartup` | true | true |
| `_initialiseOnStartup` | true | true |
| `_singletonMode` | false | true |
| `_supportedState` | Always | Always |
| `ShowConsoleKey` | None | None |
| `HideConsoleKey` | None | None |
| **`ToggleConsoleVisibilityKey`** | **Tilde** (no Ctrl/Alt/Shift) | **Tilde** (no Ctrl/Alt/Shift) |
| `SubmitCommandKey` | Return | Return |

So the key the console itself uses is **Tilde / backquote (`)**. It is not the reason the console is
missing: in the menu the GameObject is inactive, and in the lobby the `QuantumConsole` component is
disabled. Neither state ever reaches `Awake`, so `QuantumConsole.Instance` is null and the console
never initializes.

## Installer / input gate

In the lobby:

- `ConsoleInstaller` exists at `Lobby Global/Scene Context/Common Installers/Console`;
  `_console` points at the same `Quantum Console (SRP) Canvas`, `enabled=False`. The installer only
  binds the console; it does not enable it.
- `ConsoleInputHandler` exists at `Lobby Global/Input/Common Input/UI/UI_Common/Console`, is active
  and enabled, and holds the same disabled console plus `PInput.PlayerInput` / `InputStateMachines`.
  Its own methods are `OnConsoleTogglePerformed`, `OnConsoleClosePerformed`, `OnSubmitPerformed`,
  `OnConsoleActivated`, `OnConsoleDeactivated` — this is the object that is supposed to turn the
  console on from the game's input action.
- In the menu there is **no** `ConsoleInstaller` and **no** `ConsoleInputHandler`; only the inactive
  console object under `UI`.

Sending a real Tilde keypress to the foreground game window in the lobby (two presses, captured)
did **not** toggle the console. The input handler's action asset did not resolve through the
`PInput.PlayerInput.actions` accessor (returned null), so the exact gamepad/keyboard binding could
not be reproduced from the handler. The console's own Tilde handler cannot run while the component
is disabled.

## What can be shown

`QuantumConsole.Activate()` **does** display the console UI: it sets `IsActive=true` and enables the
panel; a capture exists as `qc-console-open.png` (in-game 640×360 capture) and
`qc-console-window.png` (window capture). But this is only the visual shell:

- `QuantumConsole.Instance` stays null.
- Forcing `enabled=true` is immediately undone (back to false); direct `Awake`/`Initialize`
  invocation also left `enabled=false`, `Instance=null`.
- After init, `QuantumConsoleProcessor` reports `LoadedCommandCount=0`, `TableGenerated=False`,
  `TableIsGenerating=True`; `GenerateCommandList()` returns only its header:
  `List of all commands loaded by the Quantum Processor. Use 'man' on any command to see more:`
- `InvokeCommand("help")` returns null. There are therefore **no item-give or quest-complete
  console commands** available in the pin.

The game's own quest admin path is still present as C# methods, just not reachable through this
console: `ConsoleCommands.Network.QuestsCommandsWrapper` has `CompleteCurrentTask(byte)`,
`CompleteSubTask(byte,string)`, `UncompleteSubTask`, and `CompleteAllTask(byte)`. The existing mod
already calls those through `quest.request`; nothing in this console exposes them as commands.

## Conclusion

The console is compiled and instantiated in both builds, but in the pinned build it is deliberately
dormant (inactive GameObject in the menu; disabled component in the lobby) and it never
initializes. Tilde is the correct console key, but no game input path reaches it: the
`ConsoleInputHandler` gate does not produce a working action and the component cannot be enabled
from managed code. The command table is empty even after forcing `Awake`/`Initialize`, so the
console is **compiled in but unusable for cheats**. No parallel console was built.

Captures: `qc-console-open.png`, `qc-console-window.png`. Raw probe log: `qc-probe.log`.

## Debug overlay (Task 3)

- `DebugCanvas`, `DebugFrameOverlay`, `DebugGUI`, `DebugGUISettings` types all exist in the pin.
- `Resources.Load("DebugCanvas")` succeeds in the pin; the asset is present in `resources.assets`
  in both builds, but it is not instantiated in the pin (the loaded object reports
  `activeInHierarchy=False`, and no `DebugFrameOverlay`/`DebugGUI` instance exists in Menu or Lobby:
  both counts are 0).
- The dev build's only new overlay-related literal is `Resources/DebugCanvas`; the pin has the
  asset but no caller for that path. The nine dev-only C# files (`BannerOverlay` ×3,
  `PlayerInitLog`, five Steam/lobby diagnostics) are the reference implementation for the dev
  additions, not an in-scene overlay that can be toggled on.
- A mod-side `Resources.Load` + `Instantiate` attempt did not create a visible overlay; the prefab
  instantiate returned no `GameObject` through the interop path, and a logged Unity error had no
  overlay to render it. The missing pieces are the dev build's instantiation/load call and the
  overlay components on a live object. It cannot be switched on in the pin as shipped without
  adding that wiring.
