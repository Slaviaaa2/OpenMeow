# Lab v2: hand hold and limp support

Ran against `http://127.0.0.1:17889` on 2026-07-23 with `default_mew`. Each task used `/autotune` with 40 candidates, parallelism 4, and a distinct seed. Winners were replayed and evaluated; every manual trial ended with a 1.1 s non-gripping retreat using `measureSettling: true`, and all experiments were deleted afterward. No driver profile was applied.

## Autotune winners

- `hand_hold`, seed `726101`: `candidate-028`, score **49.307**. Profile: spring 7.4196 Hz, damping 1.0792, max speed 1.6219, max acceleration 20.2494, compliance 0.49394, prediction 0.00703 s. Winner benchmark target-force gate was 0.72771; mean target force 1.1063 and post-release RMS subject speed 0.00133 m/s.
- `limp_support`, seed `726102`: `candidate-023`, score **65.832**. Profile: spring 6.7635 Hz, damping 1.2793, max speed 2.6610, max acceleration 19.5242, compliance 0.85832, prediction 0.00681 s. Winner benchmark target-force gate was 0.91707; mean target force 1.4927 and post-release RMS subject speed 0.00134 m/s.

## Manual findings

Seven trials per task covered balanced support/hold, compliant support/hold, under-damping, over-damping, light contact, ordinary excessive-force behavior, and a deliberately deep/firm contact. The full measurements are in `hand-limp.json`.

The force gate prevented weak-force exploitation: the under-forced limp trial (`meanTargetForce` 0.5464) scored only 19.273 with gate 0.28077, while the near-target limp trial (`meanTargetForce` 1.4302) scored 73.039 with gate 0.97159. A deliberate deep/firm trial reached 2.2476 force, 0.0571 m penetration, and gate 0.52779, so excessive force was also penalized. Under-damping increased jerk and contact transitions; over-damping minimized jerk but under-delivered force. Release telemetry was present for all 14 manual trials (0.9 s sampled).

Recommendation: start cross-task near spring 7.0 Hz, damping 1.2, max speed 1.3, max acceleration 9, compliance 0.95, prediction 0.015 s. This profile family retained strong gates (0.93177 hand hold, 0.97159 limp support) while giving the lowest useful release RMS values (0.00124/0.00128 m/s); its penetration (0.0296/0.0358 m) means coverage and comfort still need task-specific tuning. Prefer mean target force near the task desired force and verify the measured release window; do not select on residual motion alone.
