# Sahur Classic removal and Reel spatial audio

- [x] Inventory and remove Sahur (Classic)-only roster entries, code, assets and active references; preserved Roblox and shared assets. Historical validation evidence is retained.
- [x] Verify safe handling of old Classic selections and run focused model/build checks: retirement path and model/controls self-tests, Release build passed.
- [x] Verify 3D FMOD APIs against the pinned interop. Use the game's coordinate conversion and existing listener; no listener or mixer updates from the mod.
- [x] Trace Reel cues to their originating booths and implement directional, distance-attenuated FMOD playback; leave UI confirmation non-spatial. Focused audio tests passed and the build was deployed to both current peers.
- [x] Run focused build/checks and at most one in-game Reel interaction. Release build and focused tests passed. One host game interaction settled spin 1; both peers logged FMOD 3D lever, settle and debuff at the booth. Evidence: /home/user0/monorepo/.temp/music-reel-probe/624568eeec0149c0b4281c4b79ec7f11. Logs confirm API acceptance, not perceived panning, attenuation or audibility; physical hold dispatch was not tested.
- [x] Review changes and update this list with results. Corrected the 2D fallback to explicitly switch channel mode and corrected the documented interop flag value. Existing historical Classic notes and validation records remain as history; retirement name remains for migration.
