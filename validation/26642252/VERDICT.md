# Verdict — buildid 26642252 (`devbuild_unstable`)

Mod source snapshot: sha256 `b349ce4b…` (taken 01:05). `tracks/merged/mod` was edited by its owner at 01:46‑01:53, after this snapshot.

**compatible** — with two build-specific behavioural differences that must be recorded.

What works on this build (larger IL2CPP binary, FMOD logging DLL, Burst PDB, D3D12 debug layers,
Player Connection enabled in `boot.config`):

* interop generation from its own metadata (`Cpp2IL finished in 29.27 s`, 226 assemblies,
  157,009,651 B) on a clean process with no mod loaded;
* the mod compiles unchanged against those assemblies (0 errors; the same 32 warnings);
* host + hot assembly, generation 1 and 2, no missing-method/field exception, no native crash;
* wardrobe database 26 rows / 16 custom with clean adoption on reload;
* both custom bodies render and drive the hat anchor (`source=anchor('sahur-roblox')`,
  `source=anchor('sahur-classic')`);
* emotes: 50-entry catalogue, a ported clip and a native clip, clean hand-back;
* the game's console: live, table generated, 109 commands;
* the reel booth spawns from the saved placement store in a real map and builds the game's own
  `LookableDistantInteractable` + `DistantInteractionStaticPrompt`; the prompt object shows
  (`shown=True`, `button='e' name='interact'`);
* 125 s settled observation in a map: zero exceptions.

## Build-specific difference 1 — wardrobe selection can be reverted right after the lobby loads

`select 'Sahur'` / `select 'Sahur (Classic)'` issued immediately after the lobby became active were
overridden by the game one frame later (`SetBaseModelIndex(client=0, index=7)`, line 3082, and the
custom bodies never built: `[sahur] disposed builds=0`). Re-issuing the same request after the
wardrobe data/UI pass stuck (`wardrobe selection IS 'Sahur'`, line 5129). The identical request
sequence on 26572865 stuck first time. So the build changes the ordering/timing of the game's own
selection re-assert, not the mod's ability to select.

## Build-specific difference 2 — maps admit one player

This build's map data reports `min=1` (the lobby log line `onePlayer=on ... min=1`), so it entered a
solo map by itself at t=101 s; 26572865 reports `min=4` and needed the mod's one-player write.

## Not proven / caveats

* Physical Tilde keystroke: same foreground-lock limitation as the other build (SMOKE.md).
* map → lobby: no unattended path.
* reel interaction: `NullReferenceException` on both builds → mod defect.

Nothing in this build's behaviour required a source change, a patch, or a workaround inside the mod,
and no feature that works on 26572865 fails here.
