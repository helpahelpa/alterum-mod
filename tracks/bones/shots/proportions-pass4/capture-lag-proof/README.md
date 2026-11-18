Capture-lag proof: a capture taken in the same request that changes the scale renders the PREVIOUS state.

Sequence (one frozen rig, one camera; request order top to bottom), all with
`breastscale=1.00 chestscale=1.00` except where noted:

| file | request | md5 |
|---|---|---|
| `s_small_b-three.png` | small, second of the pair | b6131048 |
| `s_huge_a-three.png`  | **huge** (breast 2.00 / cage 1.40), first capture after the change | **b6131048** ← identical to the small state |
| `s_huge_b-three.png`  | huge again, next request | 96f3ffd3 |

`s_huge_a` is byte-identical to `s_small_b` although `breastscale`/`chestscale` changed, and the
skeleton landmarks in the two info files differ (`chest_01_l` px 384.47,294.90 vs 382.85,298.14) —
the picture is the previous request's state, not a sub-pixel coincidence. The second capture at the
same values is the real one. Every render quoted in proportions-pass4.md was taken this way.
