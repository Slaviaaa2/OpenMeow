# Responsive gait sweep v8

- Seeds: 2026072421-2026072428
- Candidates: 128 each; parallelism: 8
- Total candidates: 1024
- Failures: 0
- Evaluator versions: 8

## Winners and metrics

| Seed | Score | Stride | Slip | StopSpeed | ToeAlign | Evaluator |
|---:|---:|---:|---:|---:|---:|---:|
| 2026072421 | 75.869 | 0.5642 | 0.0574 | 0.0061 | 0.00 | 8 |
| 2026072422 | 77.265 | 0.4604 | 0.0357 | 0.0100 | 0.00 | 8 |
| 2026072423 | 76.749 | 0.4242 | 0.0245 | 0.0153 | 0.00 | 8 |
| 2026072424 | 77.285 | 0.4474 | 0.0307 | 0.0155 | 0.00 | 8 |
| 2026072425 | 77.818 | 0.4559 | 0.0379 | 0.0061 | 0.00 | 8 |
| 2026072426 | 77.664 | 0.5413 | 0.0400 | 0.0092 | 0.00 | 8 |
| 2026072427 | 78.717 | 0.5489 | 0.0309 | 0.0076 | 0.00 | 8 |
| 2026072428 | 77.45 | 0.5434 | 0.0357 | 0.0068 | 0.00 | 8 |

## Top 5 unique profiles

| Rank | Score | Stride | HipTau | Lean | SmoothTau | Plant |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 78.717 | 0.5489 | 0.0867 | 9.597 | 0.1082 | 0.8978 |
| 2 | 78.647 | 0.5381 | 0.1097 | 7.192 | 0.0854 | 0.8857 |
| 3 | 77.975 | 0.5476 | 0.0873 | 6.934 | 0.1178 | 0.8755 |
| 4 | 77.818 | 0.4559 | 0.0658 | 7.519 | 0.1171 | 0.8403 |
| 5 | 77.664 | 0.5413 | 0.1038 | 11.659 | 0.0921 | 0.8777 |

## Robust candidate

{"score": 78.717, "profile": {"bodyHeightMeters": 1.65, "hipFollowTau": 0.08674692293358238, "hipLeanDegrees": 9.597002572658008, "footSpacingMeters": 0.21, "strideLengthMeters": 0.5488638096043669, "stepHeightMeters": 0.06851691189470313, "gaitSmoothingTau": 0.10818071636515929, "turnToeDegrees": 9.09187524723442, "footPlantStrength": 0.8978299023200897}, "metrics": {"elapsedSeconds": 8.999999999999972, "plantedFootWorldSlipMetersPerSecond": 0.030873426095700995, "plantedHeightErrorMeters": 0.0013378862528426335, "swingClearanceMeters": 0.06807214899840433, "waistPeakAcceleration": 3.6587457449535057, "waistPeakJerk": 76.52436584291407, "leftRightPhaseAsymmetry": 0.01694915254237284, "stopSettlingSpeed": 0.0075529588181176945, "stopOvershootMeters": 0.30426508594710633, "toeTurnAlignmentDegrees": 1.8594011983940563e-15, "nonFinitePoseCount": 0, "swingSteps": 9, "commandedTravelMeters": 5.0591883092036305, "actualTravelMeters": 4.9572922246921065, "turnDegrees": 89.9999996577153, "movementSeconds": 6.49999999999998}}
