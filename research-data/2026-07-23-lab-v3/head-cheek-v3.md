# Lab v3 head-petting / cheek-nuzzle validation

Server: `http://127.0.0.1:17890`, subject `default_mew`, date 2026-07-23. The v2 seeds were reused unchanged: head `2026072301`, cheek `2026072302`.

## Protocol and cleanup

Each task ran `/autotune` with 32 candidates and parallelism 4. Winners were replayed with the exact benchmark path. The exact v2 slow, fast/short, and weak trajectories were replayed for each task (six manual trials total). Every sequence used `measureSettling:false` for ordinary actions and a final retreat of 1.0–1.1 s with `measureSettling:true`. All created experiments were evaluated, deleted, and `GET /experiments` returned an empty list.

## Findings

- Head winner remained `candidate-005` at **58.823** (v2 delta 0.000); cheek winner remained `candidate-030` at **57.362** (v2 delta −0.008). Both autotune result sets report `evaluator_version: 3`; winner profiles are unchanged from v2.
- Head slow scored **68.689** (delta 0), fast **0.851** (delta 0), weak/no-contact **0** (delta 0). The no-contact trial explicitly produced v3 `target_speed_naturalness=0`, `direction_reversal_naturalness=0`, and `reaction_settling=0` despite a measured retreat window.
- Cheek slow scored **52.912** (delta −0.104), fast **12.397** (delta +0.581), weak/over-compliant **26.770** (delta −0.044). The fast trial had v3 `target_speed_naturalness=0.00114` versus slow `0`; its peak jerk was 383.45 and force comfort was 0. The small nonzero fast value is the axial-speed signal, while the slow path remained outside the preferred band because its peak speed was still ~1.48 m/s.
- v3 therefore preserves the weak/no-contact gates and exposes axial target-speed telemetry. The exact trajectories are not yet slow enough to earn a high speed-naturalness value; a future controlled sweep should reduce both stroke amplitude and solver peak speed rather than relying only on longer action durations.

## v2 comparison

| Trial | v2 score | v3 score | Delta |
|---|---:|---:|---:|
| Head winner | 58.823 | 58.823 | 0.000 |
| Cheek winner | 57.370 | 57.362 | −0.008 |
| Head slow | 68.689 | 68.689 | 0.000 |
| Cheek slow | 53.016 | 52.912 | −0.104 |
| Head fast | 0.851 | 0.851 | 0.000 |
| Cheek fast | 11.816 | 12.397 | +0.581 |
| Head weak | 0.000 | 0.000 | 0.000 |
| Cheek weak | 26.814 | 26.770 | −0.044 |

No driver profile was applied.
