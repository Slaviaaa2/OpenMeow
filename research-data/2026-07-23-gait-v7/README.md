# Shared-kernel gait research v7 — 2026-07-23

This round evaluates the exact `BodyGaitKernel` used by `OpenMeow.Driver`.
`OpenMeow.Lab` supplies only a deterministic HMD trace; it no longer contains a
second foot-placement algorithm. No driver-profile apply operation was called.

## Runtime defects exposed by parity

The first parity run found three defects that the former surrogate simulator
could not reveal:

- a phase cycle already contains a left and right step, but cadence divided by
  one stride, producing about 5.85 reported steps/s;
- stopping exponentially pulled the complete accumulated phase back to zero,
  replaying old steps backwards;
- the linear stance envelope visibly dragged a nominally planted foot.

The shared kernel now uses two strides per full phase cycle, finishes only the
current half-step when stopping, reaches full stance lock before mid-stance,
and uses nonlinear plant strength (`.90` gives `.99` mid-stance lock). The Lab
metric weights planted slip by contact depth. The old Natural profile then
measured 1.846 steps/s and 0.0250 m/s weighted planted slip.

Evaluator iterations also fixed three reward defects:

- stop settling now measures the tail after deliberate deceleration;
- toe alignment measures the center of the two opposing toe oscillations only
  after residual translation has decayed;
- turn-only scenarios count rotational travel as movement coverage.

## Parallel search

Three independent neighborhoods ran eight deterministic seeds with 128
candidates each (parallelism 8):

| neighborhood | candidates | best default score |
| --- | ---: | ---: |
| Natural | 1,024 | 80.460 |
| Comfort | 1,024 | 80.163 |
| Responsive | 1,024 | 78.855 |
| **total** | **3,072** | |

The sweep score terms are identical between evaluator v6 and v7 for the mixed
default scenario; v7 only changes the movement gate for pure-turn scenarios.
Source reports are in
`../2026-07-23-gait-v6/natural-sweep.md`,
`comfort-sweep.md`, and `responsive-sweep.md`.

## v7 cross-validation

The baseline and six representative winners were replayed under default,
stop-heavy, turn-only, rapid-transition, and eight-second endurance scenarios
(35 benchmark runs, all evaluator version 7).

| candidate | minimum | mean | default | stop | turn | transitions | endurance |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Natural seed 2302 | **66.58** | **75.93** | **80.46** | **66.58** | 84.06 | **75.83** | 72.72 |
| Natural seed 2304 | 66.34 | 75.78 | 79.57 | 66.34 | **84.52** | 74.95 | 73.53 |
| Comfort seed 2311 | 66.28 | 75.75 | 80.16 | 66.28 | 84.00 | 75.66 | 72.66 |
| Comfort seed 2312 | 66.26 | 75.58 | 80.00 | 66.26 | 84.02 | 75.29 | 72.32 |
| Natural seed 2303 | 65.73 | 75.37 | 79.50 | 65.73 | 84.12 | 74.92 | 72.60 |
| previous Natural | 65.54 | 75.14 | 77.60 | 65.54 | 84.50 | 73.56 | **74.49** |
| Responsive seed 2327 | 64.39 | 73.95 | 78.86 | 64.39 | 81.90 | 72.59 | 72.00 |

The maximin winner was Natural seed 2302. A rounded profile slightly improved
its minimum and mean:

| profile | minimum | mean | default | stop | turn | transitions | endurance |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| exact | 66.58 | 75.93 | 80.46 | 66.58 | 84.06 | 75.83 | 72.72 |
| rounded | **66.62** | **75.99** | 80.33 | **66.62** | 84.05 | **75.94** | **72.99** |

## Adopted Natural body profile

`bodyHeight=1.65`, `hipFollowTau=.12`, `hipLean=9`,
`footSpacing=.20`, `stride=.53`, `stepHeight=.052`,
`gaitSmoothingTau=.20`, `turnToe=5`, `footPlant=.90`.

Body height and foot spacing remain user calibration values. The benchmark is a
flat-floor deterministic heuristic; subjective comfort, stairs, slopes,
external trackers, avatar IK, and real SteamVR use still require human testing.
