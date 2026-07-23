# OpenMeow full-body gait research — 2026-07-23

This directory contains the deterministic research run used to add and tune
OpenMeow's optional waist, left-foot and right-foot trackers.

## Run size

- Initial evaluator: 4,096 autotune candidates across Natural, Comfort,
  Responsive and local-refinement baselines.
- Evaluator v3: 3,072 candidates after cadence and anthropometric priors were
  added.
- Evaluator v4: 2,048 candidates after phase asymmetry, finite scoring and
  compliant foot planting were corrected.
- Total: 9,216 autotune candidates.
- Cross-validation: 357 phase-specific top profiles and 1,428 additional
  stop-heavy, turn-heavy, transition-heavy and endurance benchmark runs.

All research used the loopback HTTP control tower. No
`apply_gait_driver_profile` or `/gait/driver-profile/apply` operation was
called.

## What the first run found

The initial evaluator rewarded very short strides and `footPlantStrength=1`.
Its apparent robust winner used a 0.216 m stride and perfectly rigid planting.
This was reward exploitation, not a natural gait. The recorded sensitivity was
strong (`strideLengthMeters` score correlation -0.843 and
`footPlantStrength` +0.890 among 160 top records).

The harness was therefore improved before any value was adopted:

- stride phase was corrected so the configured stride means one step;
- cadence and body-proportional gait shape were added;
- body height and foot spacing became fixed user calibration values;
- phase symmetry became a measured swing-time/step-count value;
- non-finite metrics now produce a finite zero score;
- foot planting uses a nonlinear compliant blend and explicitly prefers 0.90
  over fully rigid 1.0;
- scenarios are capped at 128 segments / 120 seconds and support cancellation.

## Final v4 result

The final maximin winner was the balanced baseline itself:

| Parameter | Value |
| --- | ---: |
| `bodyHeightMeters` | 1.65 |
| `hipFollowTau` | 0.14 |
| `hipLeanDegrees` | 8 |
| `footSpacingMeters` | 0.20 |
| `strideLengthMeters` | 0.50 |
| `stepHeightMeters` | 0.07 |
| `gaitSmoothingTau` | 0.14 |
| `turnToeDegrees` | 10 |
| `footPlantStrength` | 0.90 |

Scores were 66.302 default, 64.627 stop-heavy, 65.850 turn-heavy,
65.034 transitions and 81.546 endurance. The maximin score was 64.627 and
the five-condition mean was 68.6718.

These values are now the `Natural` and default body-gait settings. Body
trackers remain disabled by default.

## Artifacts

- `*-sweep.json`: complete requests and top results from each autotune sweep.
- `cross-validation.json`, `v3-cross-validation.json`,
  `v4-cross-validation.json`: full multi-scenario results and metrics.
- `sensitivity-analysis.json`, `v4-sensitivity-analysis.json`: aggregate
  statistics and robust rankings.
- `*.stdout.log`, `*.stderr.log`: local control-tower process logs.

The simulator is deterministic and useful for relative comparisons, but it is
not a real user or a full contact-physics model. SteamVR tracker assignment,
actual avatar IK, slopes, stairs and subjective comfort still require runtime
validation.
