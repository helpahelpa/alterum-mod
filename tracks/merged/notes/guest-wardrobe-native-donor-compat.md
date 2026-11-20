# Guest wardrobe selection: native donor before the RPC (compat hot generation)

Observed live bug (2026-11-20T01:29Z): on A (guest, 24 rows) `wardrobe=select=tc2-demoman`
sent raw `16` through `WardrobeController.OnModelSelectedServerRpc`; B (host,
`-alterumDiagnosticsOnly`, 10 rows) rejected it with
`[Wardrobe] client 1 пытался занять недоступную модель 16 — отклонено.`
Evidence: `/home/user0/session-evidence/20261120T012948Z-wardrobe-select-tc2-demoman/`.

## Fix (hot side only)

The worktree already maps a custom selection to a native donor before both paths that can
write the game's row:

* `SahurWardrobe.ApplyIndex` (programmatic `select=`, `apply`, `index=`) computes
  `wireRow = NativeWirePolicy.ShippedWireIndex(...)` from the proven shipped prefix and a
  provable donor, then sends `wireRow` with `OnModelSelectedServerRpc`; it never sends
  `entry.Index`.
* `SahurWardrobe.OnLocalModelConfirmed` (Harmony prefix on the game's own
  `WardrobeController.OnModelSelected`) rewrites `chosenModelIndex` before the RPC.
* The local body/HUD keep the roster slug: the wanted record is written (guest: PENDING,
  host: landed) and the body module follows it; the wire row is only the native donor.

## Why a compat build was needed

The worktree hot referenced `IHostContext.NativeModelPrefix` (a new contract member). The
running A/B hosts load `AlterumModelSwap.Contracts.dll` sha256 `0edc3d98…`, which predates the
member; a hot generation with that MemberRef cannot bind there.

Dependency removed:

* `SahurWardrobe` proves the prefix every generation from the shipped resource set, exactly
  as `PlayerModelDatabase.Initialize` loads it:
  `Resources.LoadAll<PlayerModelData>(ResourcePathes.PlayerModelDatas)`
  (`Data/Gameplay/Player`). Rows `[0, shipped.Length)` must be the same native pointers;
  `shipped.Length` is the wire boundary. A mismatch fails closed one-way (`_prefixFailed`).
* The host-owned holder stays in the host assembly but is not consulted by hot.
* `Contracts.IHostContext.NativeModelPrefix` got a default body (`=> null`) so a sink such as
  `GameConsole.ConsoleSink` does not have to implement it; the forwarder was removed there.

Build profile: `-p:ContractsPath=<dll>` on the hot project references an explicit contracts
assembly instead of the project. The compat binary was compiled against A's deployed
contracts (`0edc3d98…`), the same file in B.

## Binary

Path: `tracks/merged/mod/AlterumModelSwap.Hot/bin/CompatRelease/AlterumModelSwap.Hot.dll`
sha256 `dc2f6855f079c4b84b2384cd4a383fc30e6f5f50dc46f278cbc45190938a542b`
md5 `4b146f8ea112eac0d765ca168174bb48`, 39,947,776 bytes, MVID `f35a17f2-26c9-4c64-ac17-4260115d5417`.

Build:

```
cd tracks/merged/mod/AlterumModelSwap.Hot
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
dotnet build -c Release -v q --nologo \
  -p:ContractsPath=/mnt/c/AlterumPinned/BepInEx/plugins/AlterumModelSwap.Contracts.dll \
  -p:OutputPath=$PWD/bin/CompatRelease/
```

## Checks

* compile: 0 errors against the deployed contracts; the csc command line contained
  `/reference:/mnt/c/AlterumPinned/BepInEx/plugins/AlterumModelSwap.Contracts.dll`. The
  default (worktree-contracts) hot and the host also build.
* metadata: 8 contract TypeRefs + 51 MemberRefs, all resolve in the deployed contracts; no
  `NativeModelPrefix` / `INativeModelPrefix` / `NativePrefixCapture` / `CaptureBeforeAppend` /
  `MarkUnknown` bytes or method defs.
* load: the hot assembly loaded in .NET against the deployed contracts (whose exports have no
  `INativeModelPrefix`); `HotModule`, `GameConsole`, `GameConsole+ConsoleSink`,
  `SahurWardrobe`, `ModelSwapRuntime` all loaded.
* tests: `tests/native-wire-guard/test_native_wire_guard.py` 20/20; its behavior harness
  17,677 checks; `tests/native-model-policy` 20 cases; `tests/native-model-prefix` 8 groups.
* the binary contains the new guest-path strings (`[model:native-wire] local selection
  client=`, `wireRow=`), which the deployed `cf28b0c4…` does not.

## Limitations

* Not deployed and no process/config touched. A/B still run the old hot; the loader will
  reload after the file is replaced (atomic temp + mv; A: `BepInEx/hot/`, B:
  `BepInEx/hot/b/`). No restart needed.
* The guest donor is the shipped row the body already carries (else the clone donor, else
  `clientId % nativeCount`). The host's wardrobe RPC accepts it only if it is the client's
  stored row or in the host's availability pool. If the host stored a different row AND the
  chosen donor is held by another connected client, the host still refuses; the client cannot
  read the host's pool. The observed pair (A stored Diana 2, host Emma 0) maps 16 -> 2 and is
  accepted (no-op or available).
* Before the prefix is proven (first frame after reload; `Resources` not loadable), the wire
  fails closed to row 0, which a host may refuse if row 0 is occupied. The custom body/HUD
  still follow the wanted slug; the host row may not change.
* A `-alterumDiagnosticsOnly` host (B) still installs no gameplay module and keeps 10 rows by
  design; the fix targets that exact case.
* Live acceptance (B accepts row 2, A body/HUD stays tc2-demoman) was not re-run because
  deployment is out of scope.

## RE feedback

`alterum-re` commits:

* `669952ebf2546cf9a350f595e2fc7b49918add2c` — `docs/NETWORKING.md`: the wardrobe selection
  RPC's availability guard.
* `c33cc34d28741eacf0ef39350a15ecaf04f47010` — `docs/DISCREPANCIES.md` row for the same.
