# Ranked canonical-joint errors (reference vs port)

> **RETIRED — DO NOT USE THESE NUMBERS.** The reference column comes from
> `scripts/ref_eval_RETIRED.py`, which was **half scale** (it omitted `ScavengerModel`'s 2.0
> factor; LC world scale is 1.1216, it applied 0.5608) and whose arm rest pose is not the retail
> one. Every centimetre in this file is 0.5x LC's world and the arm rows are not LC's arms. The
> live comparison is `../compare_vs_lc/`. See `../RETIRED.md`.

Reference = LC rig + authored curves + LC's own arm ChainIK and leg TwoBoneIK (tip rotation weight 1).
Port = in-game dump (`dump=1`), body frame (pelvis origin, model-root orientation).
`dq` = rest-corrected absolute orientation error (bone-axis bias removed); `seg` = parent->child
segment direction; `pos` = body-frame position (includes the two rigs' different proportions).
Head position is excluded from the ranking (LC head top vs port head base).

## Clap

frames 18 · body height 1.56 m · pelvis range error 0.02 cm (baseline 0.08 cm)

| joint | pos cm | seg deg | dq deg | dq baseline |
|---|---|---|---|---|
| clavicle_l | 13.8 | 113.1 | 14.4 | 14.4 |
| ball_l | 58.0 | 0.0 | 46.9 | 20.5 |
| upperarm_l | 35.8 | 86.3 | 13.2 | 13.2 |
| foot_l | 51.2 | 89.9 | 1.0 | 31.5 |
| upperarm_r | 30.7 | 60.9 | 25.8 | 25.8 |
| lowerarm_l | 44.2 | 78.0 | 8.0 | 8.1 |
| lowerarm_r | 33.6 | 51.2 | 20.4 | 20.4 |
| ball_r | 27.1 | 0.0 | 44.7 | 20.6 |

## D_Clap

frames 17 · body height 1.56 m · pelvis range error 0.02 cm (baseline 0.08 cm)

| joint | pos cm | seg deg | dq deg | dq baseline |
|---|---|---|---|---|
| clavicle_l | 13.7 | 113.1 | 14.4 | 14.4 |
| ball_l | 57.9 | 0.0 | 46.8 | 20.5 |
| upperarm_l | 35.8 | 86.2 | 13.2 | 13.2 |
| upperarm_r | 30.7 | 60.8 | 25.8 | 25.8 |
| foot_l | 50.9 | 89.9 | 1.0 | 31.5 |
| lowerarm_l | 44.2 | 78.0 | 8.0 | 8.0 |
| lowerarm_r | 33.6 | 51.2 | 20.4 | 20.4 |
| ball_r | 27.5 | 0.0 | 44.6 | 20.6 |

## D_Middle_Finger

frames 6 · body height 1.56 m · pelvis range error 0.58 cm (baseline 0.58 cm)

| joint | pos cm | seg deg | dq deg | dq baseline |
|---|---|---|---|---|
| lowerarm_l | 52.2 | 82.2 | 16.8 | 16.8 |
| ball_l | 76.0 | 0.0 | 40.7 | 20.5 |
| foot_l | 65.6 | 90.1 | 0.0 | 24.9 |
| lowerarm_r | 50.5 | 75.8 | 13.2 | 13.3 |
| clavicle_l | 11.0 | 112.8 | 14.4 | 14.4 |
| upperarm_l | 33.8 | 96.1 | 10.8 | 10.8 |
| upperarm_r | 30.0 | 83.9 | 12.6 | 12.5 |
| clavicle_r | 14.3 | 68.9 | 14.7 | 14.7 |

## D_Sign

frames 40 · body height 1.56 m · pelvis range error 0.38 cm (baseline 5.30 cm)

| joint | pos cm | seg deg | dq deg | dq baseline |
|---|---|---|---|---|
| foot_r | 40.3 | 108.4 | 33.2 | 33.2 |
| foot_l | 43.9 | 81.9 | 32.5 | 32.5 |
| clavicle_l | 12.7 | 111.3 | 14.4 | 14.3 |
| lowerarm_l | 53.4 | 83.7 | 4.3 | 4.1 |
| lowerarm_r | 48.0 | 89.4 | 4.0 | 4.1 |
| calf_l | 41.5 | 18.8 | 36.3 | 36.3 |
| calf_r | 27.8 | 24.9 | 37.7 | 37.7 |
| upperarm_r | 27.8 | 61.4 | 19.2 | 19.2 |

## Idle1Fix

frames 52 · body height 1.56 m · pelvis range error 0.35 cm (baseline 1.89 cm)

| joint | pos cm | seg deg | dq deg | dq baseline |
|---|---|---|---|---|
| ball_l | 61.9 | 0.0 | 101.1 | 20.7 |
| ball_r | 25.1 | 0.0 | 110.5 | 20.6 |
| clavicle_l | 11.9 | 113.3 | 14.4 | 14.4 |
| lowerarm_l | 45.2 | 59.3 | 23.4 | 23.4 |
| foot_l | 56.9 | 91.1 | 0.0 | 80.6 |
| lowerarm_r | 45.9 | 43.7 | 27.0 | 27.0 |
| clavicle_r | 15.1 | 67.8 | 14.7 | 14.7 |
| thigh_l | 20.1 | 41.4 | 18.3 | 4.9 |

## Middle_Finger

frames 5 · body height 1.56 m · pelvis range error 0.73 cm (baseline 0.58 cm)

| joint | pos cm | seg deg | dq deg | dq baseline |
|---|---|---|---|---|
| lowerarm_l | 52.3 | 81.9 | 16.8 | 16.8 |
| lowerarm_r | 53.4 | 66.6 | 19.3 | 19.3 |
| ball_l | 75.9 | 0.0 | 40.7 | 20.5 |
| foot_l | 65.5 | 90.1 | 0.0 | 24.9 |
| clavicle_l | 11.0 | 112.8 | 14.4 | 14.4 |
| upperarm_l | 33.8 | 95.9 | 10.7 | 10.8 |
| clavicle_r | 14.3 | 68.9 | 14.7 | 14.7 |
| foot_r | 14.6 | 82.0 | 0.0 | 18.1 |

## Prisyadka

frames 93 · body height 1.56 m · pelvis range error 1.64 cm (baseline 8.94 cm)

| joint | pos cm | seg deg | dq deg | dq baseline |
|---|---|---|---|---|
| calf_r | 56.7 | 93.2 | 51.2 | 80.9 |
| calf_l | 62.7 | 66.4 | 43.9 | 81.8 |
| ball_l | 70.3 | 0.0 | 68.6 | 20.5 |
| ball_r | 61.4 | 0.0 | 63.5 | 20.6 |
| thigh_l | 21.3 | 83.3 | 41.3 | 39.7 |
| thigh_r | 13.5 | 83.8 | 40.6 | 42.3 |
| clavicle_l | 33.0 | 114.6 | 14.7 | 14.7 |
| upperarm_r | 41.8 | 85.4 | 15.5 | 15.5 |

## Salute

frames 19 · body height 1.56 m · pelvis range error 0.54 cm (baseline 3.37 cm)

| joint | pos cm | seg deg | dq deg | dq baseline |
|---|---|---|---|---|
| clavicle_l | 14.4 | 113.3 | 14.4 | 14.4 |
| upperarm_r | 31.9 | 80.6 | 15.3 | 14.7 |
| foot_l | 47.0 | 92.4 | 0.7 | 16.5 |
| lowerarm_r | 57.3 | 61.0 | 9.1 | 8.8 |
| clavicle_r | 17.2 | 67.7 | 14.7 | 14.7 |
| ball_l | 54.8 | 0.0 | 28.3 | 20.5 |
| foot_r | 18.6 | 83.6 | 1.0 | 19.9 |
| lowerarm_l | 39.7 | 27.4 | 13.6 | 13.6 |

## Shy

frames 29 · body height 1.56 m · pelvis range error 0.45 cm (baseline 2.49 cm)

| joint | pos cm | seg deg | dq deg | dq baseline |
|---|---|---|---|---|
| clavicle_l | 11.2 | 111.4 | 14.4 | 14.4 |
| foot_l | 51.0 | 89.3 | 3.5 | 15.7 |
| lowerarm_l | 46.8 | 81.6 | 8.4 | 8.4 |
| lowerarm_r | 42.6 | 65.1 | 8.2 | 8.2 |
| ball_l | 50.1 | 0.0 | 36.5 | 20.5 |
| clavicle_r | 16.3 | 71.9 | 14.7 | 14.7 |
| foot_r | 15.5 | 81.9 | 3.5 | 11.1 |
| upperarm_l | 31.2 | 40.0 | 13.0 | 13.1 |

## Sign

frames 24 · body height 1.56 m · pelvis range error 0.11 cm (baseline 0.60 cm)

| joint | pos cm | seg deg | dq deg | dq baseline |
|---|---|---|---|---|
| foot_r | 35.5 | 108.5 | 33.1 | 33.1 |
| foot_l | 44.1 | 81.9 | 32.5 | 32.5 |
| lowerarm_l | 58.4 | 91.7 | 11.5 | 11.5 |
| lowerarm_r | 53.9 | 90.0 | 12.0 | 12.2 |
| clavicle_l | 12.6 | 110.8 | 14.4 | 14.4 |
| calf_l | 39.0 | 18.8 | 32.9 | 32.9 |
| upperarm_r | 29.6 | 58.7 | 16.7 | 16.7 |
| clavicle_r | 14.8 | 72.9 | 15.6 | 15.5 |

## The_Griddy

frames 89 · body height 1.56 m · pelvis range error 2.25 cm (baseline 12.38 cm)

| joint | pos cm | seg deg | dq deg | dq baseline |
|---|---|---|---|---|
| clavicle_l | 36.2 | 106.3 | 14.0 | 14.1 |
| calf_l | 47.1 | 74.4 | 21.4 | 74.1 |
| clavicle_r | 41.0 | 78.9 | 15.0 | 15.0 |
| foot_l | 57.2 | 84.6 | 1.8 | 24.7 |
| calf_r | 22.6 | 79.4 | 21.5 | 117.4 |
| ball_r | 56.6 | 0.0 | 39.5 | 20.7 |
| lowerarm_r | 48.6 | 49.8 | 17.5 | 17.5 |
| lowerarm_l | 48.0 | 54.4 | 15.2 | 15.2 |

## Twerk

frames 24 · body height 1.56 m · pelvis range error 0.30 cm (baseline 18.24 cm)

| joint | pos cm | seg deg | dq deg | dq baseline |
|---|---|---|---|---|
| ball_l | 87.7 | 0.0 | 57.9 | 22.4 |
| clavicle_l | 45.0 | 111.6 | 14.4 | 14.6 |
| foot_l | 75.0 | 89.5 | 3.9 | 41.6 |
| calf_l | 51.5 | 42.3 | 38.9 | 34.7 |
| ball_r | 39.2 | 0.0 | 65.3 | 23.0 |
| clavicle_r | 45.7 | 71.6 | 14.7 | 15.0 |
| calf_r | 46.5 | 36.8 | 29.6 | 46.4 |
| foot_r | 21.1 | 81.2 | 3.8 | 48.1 |
