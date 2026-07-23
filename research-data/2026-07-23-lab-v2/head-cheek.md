# Evaluator v2: head petting and cheek nuzzle

Run date: 2026-07-23. Server: `http://127.0.0.1:17889`. Subject: `default_mew`.

## Protocol

`/autotune` used 32 candidates at parallelism 4 with deterministic, distinct task seeds (`2026072301` head and `2026072302` cheek). The tuned winner was then replayed with the exact benchmark trajectory to collect v2 metrics. Eight manual/adversarial trials (four per task) covered weak force, excessive speed/short contact, slow multi-stroke contact, and damping/prediction variants. Each sequence had ordinary actions with `measureSettling:false` and one final retreat of at least 1.0 s with `measureSettling:true`. All experiments were deleted; `GET /experiments` was empty.

## Results

- Head petting autotune winner: `candidate-005`, score **58.823**. Profile: spring 4.890 Hz, damping 0.921, max speed 1.690, max acceleration 9.721, compliance 0.855, prediction 0.0077 s. Exact benchmark metrics: 2.356 s contact (1.067 s target), mean force 0.484, peak force 1.298, jerk 31.77, penetration 0.01155, two contact transitions, and 0.9 s post-contact sampling. v2 components include target coverage 0.3519, target-force gate 0.8924, target-speed naturalness **0**, and evaluator version 2.
- Cheek nuzzle autotune winner: `candidate-030`, score **57.370**. Profile: spring 7.941 Hz, damping 1.264, max speed 1.483, max acceleration 14.978, compliance 0.777, prediction 0.0229 s. Exact benchmark metrics: 1.456 s contact/target contact, mean force 0.670, peak force 1.946, jerk 98.28, penetration 0.01410, 14 transitions, and 0.9 s post-contact sampling. v2 components include target coverage 0.4802, target-force gate 0.9592, target-speed naturalness **0**, and evaluator version 2.
- Weak-force trials were rejected: head scored **0.000** with no contact; cheek scored **26.814** with very low force comfort (0.0354) and penetration safety (0.3646), despite target coverage 0.7069. High compliance does not create a free high score.
- Excessive speed/short contact was strongly rejected: head **0.851** and cheek **11.816**. Contact lasted only 0.011/0.056 s, target speed reached 5.92/3.59, and jerk reached 288/372; cheek peak-force safety fell to 0.515.
- Slow multi-stroke trajectories produced the best manual head result (**68.689**) and near-complete cheek target coverage (**0.99899**, score **53.016**), but both still received target-speed naturalness **0**. The simulated contact speeds (0.748 head, 0.467 cheek) remain above the task-preferred bands, so “slow” should be slowed further (longer stroke durations) for a naturalness pass.
- High damping plus long prediction was not a general improvement (head **30.670**, cheek **49.936**); it reduced target coverage and retained target-speed penalties.

## Conclusion

Evaluator v2 does reject weak/fast gaming. Weak contact is gated to zero or low comfort, and short high-speed bursts are exposed by target-contact speed, force-fit, jerk, and peak-force components even when contact continuity is superficially good. The most natural direction is sustained, compliant contact with repeated reversals and a measured retreat; however, the current 0.34–0.60 s stroke timings are still too fast for v2’s preferred speed bands. Start from the task-specific winners (head `candidate-005`, cheek `candidate-030`), lower stroke speed substantially, and preserve the final measured retreat when moving to human/driver validation. No driver profile was applied.
