# OpenMeow Lab v3 cross-task decision — 2026-07-23

Endpoint `http://127.0.0.1:17890`, evaluator v2, subject `default_mew`, fixed seeds 424242/230723/230724/230725 for head/cheek/limp/hand.

## Scope and cleanup

- Five fixed profiles × four tasks = 20 standard trajectories. Every sequence ended with an explicit non-gripping retreat of 1.0 s and `measureSettling:true` (0.90 s sampled).
- Autotune: 24 candidates per task (96 candidates total, parallelism 4).
- Four final retreat checks used the selected common profile. All experiments were deleted; final experiment list was empty.

## Autotune

| Task | Best profile | Score |
|---|---|---:|
| head_petting | candidate-012 | 69.380 |
| cheek_nuzzle | candidate-021 | 64.440 |
| limp_support | candidate-021 | 70.780 |
| hand_hold | candidate-018 | 63.290 |

## Standard-profile ranking

Mean and worst are across the four standard task trials. Force gate is the mean/worst multiplicative v2 gate; speed is mean `target_speed_naturalness`; settling is mean `reaction_settling`.

| Profile | Parameters (Hz, damping, speed, accel, compliance, prediction) | Mean | Worst | Force gate mean/worst | Axial speed naturalness | Settling |
|---|---|---:|---:|---:|---:|---:|
| `hand-limp-recommendation` | 7, 1.2, 1.3, 9, .95, .015 | **58.41** | 48.03 | .838/.742 | 0.000 | .997 |
| `natural-current` | 6.5, 1.2, 1.7, 18, .80, .030 | 57.97 | 39.61 | .823/.662 | 0.000 | .996 |
| `v1-soft` | 6, 1.1, 1.5, 14, .78, .020 | 56.84 | **51.31** | .811/.698 | 0.000 | .997 |
| `task-specific-v2-like` | 8.5, 1.2, 1.9, 20, .82, .030 | 53.70 | 33.54 | .799/.593 | 0.000 | .996 |
| `aggressive-control` | 10, .85, 2.8, 28, .40, .040 | 27.89 | 19.83 | .632/.467 | 0.000 | .999 |

Per-task standard winners: head `v1-soft` 55.45, cheek `natural-current` 68.19, limp `hand-limp-recommendation` 74.38, hand `v1-soft` 58.30. The maximin choice is `v1-soft` (worst 51.31); the mean-only choice would be the hand/limp profile, whose hand score falls to 48.03.

## Desktop Natural recommendation

Use `v1-soft` as the single Natural hand profile:

```json
{"positionSpringHz":6.0,"dampingRatio":1.10,"maxSpeed":1.5,"maxAcceleration":14.0,"contactCompliance":0.78,"predictionSeconds":0.020,"handRadius":0.075}
```

The only driver call was `POST /driver-profile/preview` with `basePreset:"Natural"`; it returned second-order smoothing, 6 Hz spring, damping 1.1, max speed 1.5, max acceleration 14, prediction .02, and `applied:false`. No apply call was made.

## Axial speed, force, and settling observations

For the selected profile, mean/peak target-contact speeds were head 0.81/1.30, cheek 0.34/1.30, limp 0.52/1.32, and hand 0.16/0.56 m/s. Despite these measured speeds, v2 `target_speed_naturalness` rounded to 0 on all contacted standard trials because the pilot-like action durations exceed each task's preferred speed range. The force gate remained useful (selected-profile task gates .87, .83, .70, .85), preventing under-forced coverage from scoring as a success. All explicit retreats measured 0.90 s and produced settling near 1.00.

## Confidence and caveats

Confidence is moderate for choosing the maximin profile within this simulator, but low for real-world hand feel: only four deterministic trajectories per profile were used, and the axial-speed component is effectively saturated at zero. A no-contact path can still receive near-perfect settling because the retreat measurement observes no subject impulse; settling must therefore be read with coverage and force gate. The aggressive control is consistently worse and should not be used as Natural defaults.

Raw responses, metrics, profile definitions, preview, and cleanup evidence are in [`generalization-v3.json`](./generalization-v3.json).
