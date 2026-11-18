# Dev-build debugging hooks — opt-in tools (not installed)

Source: `C:\AlterumCandidates\devbuild_unstable` (Steam `devbuild_unstable`, buildid 26642252).
Target if ever used: a **copy** of the pin such as `C:\AlterumDev`, never `C:\AlterumPinned`,
`C:\AlterumPinned-pristine`, or the live Steam install.

**Nothing here is installed in the pin.** Each item is a separate debugging aid, listed with what
it enables, whether it can be dropped in on its own, and its cost/risk.

## Summary

| File (dev build) | Size | Enables | Independent drop-in? | Main risk |
|---|---:|---|---|---|
| `Alterum_Data\Plugins\x86_64\fmodstudioL.dll` | 4,355,072 | FMOD Studio **logging** build | Only as a replacement for `fmodstudio.dll`; not alongside it | Very noisy logs; ABI/version must match the integrated FMOD |
| `Alterum_Data\boot.config` (Player Connection lines) | ~0.5 KB text | Unity **Player Connection** (profiler / debugger attach) | Yes, as a merge, not a file replace | Opens a TCP listener; dev-only surface; binds to the dev author's IPs if copied verbatim |
| `WinPixEventRuntime.dll` (game root) | 58,368 | PIX GPU-capture event markers for D3D12 | Yes | None while unused; PIX version must match what the game looks for |
| `D3D12\d3d12SDKLayers.dll` | 4,930,104 | D3D12 **validation/debug layer** | Yes, file placement; but the game must also request the debug device | Large CPU/GPU slowdown, validation spam, possible device-creation failure |
| `Alterum_Data\Plugins\x86_64\lib_burst_generated.pdb` | 12,050,432 | Burst job symbols (readable Burst stacks) | No, not with the pin's Burst DLL | PDB matches the dev build's Burst DLL (3,379,200 B), not the pin's (3,175,424 B) |

## Details

### `fmodstudioL.dll` — FMOD logging build

- The dev build ships the **L** (logging) variant of FMOD Studio; the pin ships the normal
  `fmodstudio.dll` (3,248,128 B). FMOD logging builds write internal mixer/event diagnostics.
- A Unity/FMOD integration loads one library name. The dev build runs with `fmodstudioL.dll`
  present and `fmodstudio.dll` absent, so its integration is configured for the logging name.
  Dropping `fmodstudioL.dll` into the pin **next to** `fmodstudio.dll` normally does nothing,
  because the pin's integration still asks for `fmodstudio.dll`.
- To actually use it, replace the pin's `fmodstudio.dll` with this file (renamed), which is a
  runtime-library swap, not a side-by-side tool. FMOD's API/ABI must match the game's integration;
  a mismatch fails at `FMOD_System_Create`/bank load.
- Risk: log volume is large (every event/mixer action), and the logging build is slower. Do this
  only for an FMOD-specific session, on a copy.

### `boot.config` — Unity Player Connection

Dev build adds these lines to `Alterum_Data\boot.config`:

```
player-connection-mode=Listen
player-connection-guid=88160013
player-connection-debug=0
player-connection-project-name=Alterum
player-connection-ip=<lan-address-1>
player-connection-ip=<vpn-address-1>
player-connection-ip=<lan-address-2>
```

- This is Unity's **Player Connection**: the player listens for the Unity Editor/profiler and
  accepts a profiler attach. It enables CPU/GPU profiling, memory snapshots and managed debugging
  of the running player, which is otherwise unavailable in a release build.
- Independent drop-in: **yes, as a merge**, not a file copy. Keep the pin's existing keys and
  `build-guid=2aae71e377394b93b6d7626d4552d658`; add only the `player-connection-*` keys. Copying
  the dev file wholesale would change the build identity and add three hard-coded IPs.
- Risk: it opens a listening socket and accepts profiler connections. The three IPs above are the
  dev author's LAN/Tailscale addresses; for local work use `player-connection-mode=Listen` with no
  `player-connection-ip` (or bind to loopback) and close the player when done. It is a debug
  surface, not something to leave enabled for normal play.

### `WinPixEventRuntime.dll` — PIX markers

- The PIX event runtime lets a D3D12 title emit named GPU events/counters that **PIX for Windows**
  can capture. It does not itself capture anything; it must be used with a PIX capture.
- Independent drop-in: **yes**. Put it in the game root (or next to `Alterum.exe`) in a copy. It
  is inert when no PIX capture is running.
- Risk: minimal. A mismatched PIX runtime/`D3D12Core.dll` combination can fail to load, but the
  game does not depend on it; remove it if a capture is not in progress.

### `D3D12\d3d12SDKLayers.dll` — D3D12 validation layer

- This is Microsoft's D3D12 SDK Layers DLL. It provides the debug/validation layer: API validation,
  resource-state tracking, shader debug info and GPU-based validation when the device is created
  with `D3D12_CREATE_DEVICE_DEBUG`.
- Independent drop-in: **partly**. Placing the DLL in `D3D12\` next to `D3D12Core.dll` is enough
  for the loader to find it, but validation only turns on if the game/Unity creates the device with
  the debug flag (Unity's `-force-d3d12-debug` / D3D12 debug-device setting). Presence alone is
  not a switch.
- Risk: high. Validation is commonly 2–10× slower, produces enormous log spam, and can make the
  device fail to create if the Windows "Graphics Tools" optional feature / developer mode is not
  installed. GPU-based validation is slower still. Use only for a short rendering investigation on
  a copy, never for gameplay or captures.

### `lib_burst_generated.pdb` — Burst symbols

- The dev build is the only build shipping a PDB, and it is for **Burst-generated jobs**. It turns
  Burst frames in crash dumps/profiler stacks into file/line symbols instead of addresses.
- Independent drop-in: **no, not for the pin**. The PDB is matched to the dev build's
  `lib_burst_generated.dll` (3,379,200 B); the pin's is a different binary (3,175,424 B). Loading
  the dev PDB against the pin's DLL gives wrong or no symbols. To use it you must also swap in the
  dev build's Burst DLL, which is a separate binary change and not warranted just for symbols.
- Risk: none at runtime (PDBs are not loaded by the game), but a mismatched PDB is worse than no
  PDB because it yields confidently wrong stacks. Keep it with the dev build.

## Recommended use

Copy the pin to a scratch install (`win\make-instance.ps1 -Src C:\AlterumPinned -Dst C:\AlterumDev`),
apply **one** hook there, reproduce, collect the log/capture, then discard the copy. Never stack
validation layers with FMOD logging and Player Connection at the same time; the noise makes the
result unreadable and the slowdown makes timing meaningless.
