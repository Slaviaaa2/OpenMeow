# Shared-kernel gait research v8 — 2026-07-23

v8 closes the remaining gap between the research stimulus and the desktop
driver. The default Lab trace now uses the Natural desktop settings exactly:
0.9 m/s forward/strafe, 60°/s turn and 0.18 s input smoothing. Scenario segments
also accept a sanitized `speedMultiplier` (`.1..3`) so Slow `.25`, Normal `1`
and Fast `2.25` can be tested through the same kernel. No apply operation ran.

## Parallel search

Natural, Comfort and Responsive neighborhoods each ran eight deterministic
128-candidate searches at parallelism 8:

| neighborhood | candidates | best default score |
| --- | ---: | ---: |
| Natural | 1,024 | 80.506 |
| Comfort | 1,024 | 79.933 |
| Responsive | 1,024 | 78.717 |
| **total** | **3,072** | |

All 24 responses reported evaluator version 8 and zero failures. The detailed
seed tables are in `natural-sweep.md`, `comfort-sweep.md` and
`responsive-sweep.md`.

## Seven-scenario cross-validation

The current baseline and four representative winners were replayed under
default, stop-heavy, pure-turn, rapid-transition, endurance, Fast 2.25× and
Slow .25× scenarios (35 runs).

| candidate | min | mean | default | stop | turn | transitions | endurance | fast | slow |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Natural seed 2404 | **67.40** | 76.20 | 80.27 | 69.51 | **80.33** | **67.40** | **75.23** | 76.25 | **84.41** |
| prior baseline | 66.85 | **76.46** | 80.02 | **69.69** | 79.63 | 66.85 | 75.00 | **79.76** | 84.25 |
| Natural seed 2401 | 66.31 | 75.94 | **80.51** | 69.13 | 79.45 | 66.31 | 75.74 | 76.61 | 83.86 |
| Responsive seed 2427 | 66.27 | 75.34 | 78.72 | 68.13 | 78.75 | 66.27 | 74.12 | 78.21 | 83.22 |
| Comfort seed 2411 | 66.14 | 75.74 | 79.93 | 68.96 | 79.41 | 66.14 | 75.29 | 76.78 | 83.65 |

Natural seed 2404 is the maximin winner. Its rounded form slightly improves
minimum and mean:

| profile | min | mean | default | stop | turn | transitions | endurance | fast | slow |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| exact | 67.40 | 76.20 | 80.27 | 69.51 | 80.33 | 67.40 | 75.23 | 76.25 | 84.41 |
| rounded | **67.42** | **76.23** | 80.26 | **69.57** | **80.36** | **67.42** | 75.19 | **76.34** | **84.45** |

## Adopted Natural body profile

`bodyHeight=1.65`, `hipFollowTau=.08`, `hipLean=11`,
`footSpacing=.20`, `stride=.45`, `stepHeight=.06`,
`gaitSmoothingTau=.24`, `turnToe=7`, `footPlant=.92`.

The shared kernel additionally calibrates its floor from the first/reset HMD
height, starts with both feet planted at phase boundaries, reports per-tracker
yaw velocity, and rejects nonfinite or negative evaluation metrics. This remains
a deterministic flat-floor heuristic: subjective comfort, avatar IK, slopes,
stairs and physical SteamVR use need human validation.
