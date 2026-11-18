# Controls rows: why resolution failed, and the fix

Addendum to `MERGE-MANIFEST.md`. Clone `C:\AlterumI` (instance `i`). **Instance A untouched — not deployed.**

## The actual cause of `resolved=0/34`

The integrator's diagnosis ("`ResolveActionAndBinding` throws for a runtime-built asset") was half
right: it does throw, but not because of the asset. Measured field by field on a mod row and on a
native row as the control (`menu.request: diag`, `refprobe`, `piprobe`):

| | mod row | native row |
|---|---|---|
| `m_Action` / `actionReference` | set, runtime asset `AlterumModBindings` | set, asset `HandheldControls` |
| `ref.action`, `ref.asset` | resolve fine | resolve fine |
| `m_BindingId` ↔ `action.bindings[i].id` | **matched, index 0** | matched, index 0 |
| **`row._playerInput`** | **`null`** | `Input` (`PInput.PlayerInput`) |
| `ResolveActionAndBinding` | **throws `NullReferenceException`** | `True` |

IL2CPP stack of the throw, which names the frame:

```
System.NullReferenceException
  at HWG.Settings.Controls.RebindActionUI.GetInputAction (InputActionReference actRef)
  at HWG.Settings.Controls.RebindActionUI.ResolveActionAndBinding (InputAction&, Int32&)
  at HWG.Settings.Controls.RebindActionUI.UpdateBinding ()          <- UpdateBindingDisplay path
```

`RebindActionUI.GetInputAction(actRef)` delegates to `PInput.PlayerInput.GetInputAction`
(PInput.dll: `M InputAction GetInputAction(InputActionReference)`). Two independent faults:

1. **`_playerInput` is `null` on a clone.** It is a runtime cache, not serialized, so
   `Object.Instantiate` does not copy it and nothing re-seats it — hence the NRE. Bisected by
   handing the same row a matrix of reference shapes: *every* reference threw (V0–V5), and the
   native row + our reference did **not** throw (`GetInputAction` → `null`). The throw followed
   the **row**, not the reference.
2. **A detached asset can never resolve anyway.** With `_playerInput` seated, the NRE becomes a
   clean `ok=False action=<null>`: `PlayerInput.GetInputAction` only returns an action whose
   `InputActionAsset` is one of `PlayerInput.InputActionAssets`. A `FromJson` asset is not.

So the mod's original deliberate separation ("keep our actions out of `PlayerInput.InputActionAssets`
so the game's rebind save cannot own them") is exactly what made the rows unresolvable. The
fallback the manifest recorded (write `m_Action`/`m_BindingId` directly) removed the *creation*
crash but could never produce `resolved=1`.

## Fix (`ModControlsUI.cs`)

* The action map is **added to a live game asset** (`PlayerInput.PlayerActionsControls._asset`; its
  wrapper type is not an `InputActionAsset`, so the backing field is the asset) instead of a
  `FromJson` asset. Chords use `AddCompositeBinding("ButtonWithOneModifier")`.
* Each clone is **seated with `row._playerInput`** (the live `PInput.PlayerInput`, donor fallback)
  and `row._rebindOverlay` from the donor, before the game's own refreshers run.
* `m_BindingId` is the id the **hosted asset actually produced** (`action.bindings[0].id`), not the
  GUID a detached JSON asset would have carried.
* Registry → action on build: a stored override is re-applied with
  `ApplyBindingOverride(0, Current.Path)`, otherwise a reload rebuilt the map at the default while
  the store still held the user's key, and the mirror reverted it (measured `'G' → 'F5'`).
* Composite rows mirror their **button part** (`Shift+<Keyboard>/x`), not the head
  `ButtonWithOneModifier` (which the store cannot poll).
* **Stale rows are destroyed on build.** The game's own list population clones the row container,
  so our rows were duplicated with it: `destroyed 7038 row(s)` after 9 reloads
  (`rows 6860 → 6962 → 6996 → 7030 …`). Without this the Controls list grows without bound and dead
  rows show broken text.
* `MenuProof.Reset` now calls the row's native `ResetToDefault()` first. Clearing only the store was
  undone by the mirror half a second later (measured: reset → F5, then G again).

Fallback retained honestly: if no `PlayerInput`/asset exists, the detached asset is used and the rows
are **read-only** — `Button.interactable = false`, `RebindActionUI.enabled = false`,
label `Alterum (read-only): …`, key text `… (fixed)`.

## What is measured (clone `i`, generation 12, `[Error]` = 0, `SWAP` = 0)

| step | evidence |
|---|---|
| map hosted | `hosted map 'AlterumMod' in game asset 'PlayerActionsControls' mapActions=34 mapBindings=38 assetMaps=11 playerInput='Input'` |
| resolution | `built 34 rows … resolved=34/34` (was `0/34`); `diag` → `ResolveActionAndBinding=True`, `UpdateBindingDisplay=ok`, `DisplayString=leftArrow` |
| override → store | `[bind] override id=mod.clothes.toggle 'F5' -> 'G' source=controls` |
| survives reload | `overridesReapplied=1`, then `effective=G override=yes` |
| reset restores default | `[menu] reset id=mod.clothes.toggle ok=True nativeRowReset=True effective='F5'`, next poll `effective=F5 override=no` |
| row shows its own binding | `m_BindingText='leftArrow'` (native `'f'`), row label `Alterum: Picker: left` |

## Not proven

* **The physical keypress inside the rebind dialog.** `StartInteractiveRebind()` runs
  (`ongoing=True`), but completing it needs a real key event delivered to the game window. This
  session is WSL over SSH in **session 0**; `EnumWindows` finds no window for pid 66744 and
  `run-in-session1.ps1` produced no observable keystroke. The override/reset/reload chain above was
  driven through the native methods the rebind operation itself calls on completion
  (`ApplyBindingOverride` + `UpdateBinding`), not through a key.
* Consequently **"dispatch uses the new key / the old key stops working" done by pressing keys is
  unproven**. What is proven is that the polled value moved (`ModBindings` effective `F5 → G → F5`)
  and that `ClothesToggle.PollHotkey` reads exactly that value.

## Changes

`ModControlsUI.cs` (hosted asset, `_playerInput` seating, binding id, override re-apply, composite
mirror, stale-row sweep, `MakeReadOnly`, `Diag`/`RefProbe`/`PlayerInputProbe` verification hooks),
`MenuProof.cs` (`diag`, `refprobe`, `piprobe` commands; native-first reset), 
`AlterumModelSwap.Hot.csproj` (+`PInput` interop reference, `Private=false`),
`win/sendkey-i.ps1` (new, key injection helper; targets the clone by pid, never the Steam install).
Build: `dotnet build -c Release`, exit 0, 0 `error CS`. DLL md5 `dc38ef8baaea0d1b6655a33cfd546d44`.
