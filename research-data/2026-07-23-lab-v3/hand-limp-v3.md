# Lab v3 validation: hand hold and limp support

Validated against `http://127.0.0.1:17890` on 2026-07-23 using the prior v2 seeds (`726101` and `726102`). Each `/autotune` used 32 candidates and parallelism 4. The v2 autotune winners were reproduced exactly: `hand_hold` candidate-028 scored 49.307 and `limp_support` candidate-023 scored 65.832 (both score deltas 0.000; evaluator version 3).

The prior cross-task profile (7 Hz / damping 1.2 / max speed 1.3 / max acceleration 9 / compliance 0.95 / prediction 0.015) scored 65.091 on hand hold and 74.357 on limp support: mean 69.724, worst 65.091. Against v2's matching manual runs, deltas were +3.966 and +1.318 (mean +2.642). The v1-soft profile (6 Hz / 1.1 / 1.5 / 14 / 0.78 / 0.02) scored 68.282 and 65.458: mean 66.870, worst 65.458. There was no exact v2 v1-soft trial, so those deltas are intentionally unreported.

All four valid trials ended in a marked 1.1 s non-gripping retreat with `measureSettling: true` and sampled 0.9 s. The prior profile retained force gates 0.97146 (hand) and 0.98797 (limp), with post-release RMS subject speeds 0.00124 and 0.00128 m/s. v1-soft had lower penetration but slightly higher residual RMS and a weaker limp gate (0.85021).

The no-contact control used a far-away path and deliberate retreat. It scored 0; `target_coverage`, `stroke_naturalness`, `reaction_settling`, `target_force_gate`, `target_force_fit`, `target_speed_naturalness`, and `direction_reversal_naturalness` were all exactly zero, while the settling window was still measured. All five validation experiments were deleted; `/experiments` was empty afterward. No driver profile was applied.
