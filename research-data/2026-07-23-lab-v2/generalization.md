# Lab v2 generalization study — 2026-07-23

Endpoint: `http://127.0.0.1:17889`; evaluator version 2; subject `default_mew`; fixed seeds: 424242 (head), 230723 (cheek), 230724 (limp), 230725 (hand).

## Experiments

- Autotune: 40 candidates × 4 tasks (160 candidates total), parallelism 4.
- Common-profile re-evaluation: 8 fixed profiles × 4 tasks × 3 trajectories = 96 trials.
- Trajectories: pilot-like benchmark, weak-contact (contact targets 8 cm outward), and aggressive (0.35× action durations, 5 cm deeper contact). Every sequence ended with `measureSettling:true` and a 1.0–1.1 s retreat; each recorded 0.90 s post-contact data.
- Cleanup: all 100 created experiments were deleted; final `GET /experiments` was empty.

## Autotune results

| Task | Candidates | Best profile | Best score |
|---|---:|---|---:|
| head_petting | 40 | baseline | 63.320 |
| cheek_nuzzle | 40 | candidate-017 | 63.582 |
| limp_support | 40 | candidate-023 | 72.550 |
| hand_hold | 40 | candidate-018 | 62.023 |

## Common-profile ranking

Scores are over all 12 trials per profile; `std` is the four benchmark-like trials only. Force-gate and speed columns are means over all variants; settling is the mean `reaction_settling`.

| Profile | All mean / worst | Std mean / worst | Weak mean | Aggressive mean | Force gate mean / worst | Speed naturalness | Settling |
|---|---:|---:|---:|---:|---:|---:|---:|
| aggressive | 40.69 / 0.00 | 54.81 / 40.78 | 20.92 | 46.35 | 0.638 / 0.000 | 0.030 | 0.998 |
| v1-pilot-soft | 29.16 / 0.00 | **56.84 / 51.31** | 2.51 | 28.13 | 0.421 / 0.000 | 0.061 | 0.998 |
| weak-contact | 28.94 / 0.00 | 56.44 / 41.96 | 20.63 | 9.75 | 0.482 / 0.000 | 0.046 | 0.998 |
| balanced-v2 | 28.59 / 0.00 | 53.70 / 33.54 | 4.97 | 27.10 | 0.460 / 0.000 | 0.046 | 0.998 |
| natural-current | 27.91 / 0.00 | 56.25 / 41.24 | 3.51 | 23.97 | 0.411 / 0.000 | 0.061 | 0.998 |
| underdamped-high-speed | 26.48 / 0.00 | 17.89 / 0.00 | 30.61 | 30.93 | 0.639 / 0.000 | 0.015 | 0.997 |
| overdamped-low-speed | 18.44 / 0.00 | 34.23 / 26.88 | 1.00 | 20.10 | 0.289 / 0.000 | 0.081 | 0.999 |
| v1-pilot-mid | 15.04 / 0.00 | 24.60 / 14.30 | 6.30 | 14.21 | 0.260 / 0.000 | 0.046 | 0.999 |

The standard-suite maximin winner is `v1-pilot-soft` (mean 56.84, worst task 51.31), narrowly ahead of `natural-current` on worst task. The global adversarial worst case is 0 for every profile because deliberate weak/aggressive paths can miss target contact; this is a useful robustness signal, not a reason to apply a profile blindly.

## Mapping and desktop recommendation

Per-task standard winners were head `natural-current` (63.43), cheek `weak-contact` (64.94), limp `balanced-v2` (72.69), and hand `aggressive` (70.71). For one desktop Natural hand profile, choose the cross-task maximin `v1-pilot-soft`:

```json
{"positionSpringHz":6.0,"dampingRatio":1.10,"maxSpeed":1.5,"maxAcceleration":14.0,"contactCompliance":0.78,"predictionSeconds":0.020,"handRadius":0.075}
```

The one allowed `POST /driver-profile/preview` used `basePreset:"Natural"` and this profile. It returned second-order smoothing, spring 6 Hz, damping 1.1, max speed 1.5, max acceleration 14, prediction 0.02, and `applied:false`. The full response is in `generalization.json`; no `/apply` request was made.

## Evaluator loopholes / caveats

- The force gate is multiplicative and correctly drives missed-contact trials to score 0; coverage cannot rescue a zero gate.
- The explicit retreat window gives settling ≈1.0 even when the trajectory never contacts (no subject impulse). Settling is therefore not evidence of successful interaction by itself.
- Pilot-like stroke durations produce target-speed naturalness ≈0 on contacted benchmark trials (the simulated target-contact speeds exceed each task's preferred range). Zero-contact adversaries receive a nonzero default speed-naturalness component (~0.18), so that component should be interpreted with contact coverage and force gate.
- Mean force is often below the task target despite nonzero coverage; the v2 gate exposes this (e.g. chosen profile standard force gates 0.70–0.87). Do not rank by coverage alone.

Raw deterministic responses, metrics, components, profile definitions, preview, and cleanup evidence: [`generalization.json`](./generalization.json).
