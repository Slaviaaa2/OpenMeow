# Gait v1 sensitivity notes

- Records: 160 top candidates across four sweeps.
- Score range: 61.468–69.650; mean 65.586.
- FootPlantStrength saturated at 1.0 in 58/160 (36.3%). This indicates a one-sided evaluator incentive, not proof that perfectly rigid planting feels best.
- Strongest absolute score correlations: footPlantStrength 0.890, strideLengthMeters -0.843, bodyHeightMeters 0.405, stepHeightMeters -0.395, hipLeanDegrees -0.345.
- Weakly identified (|r| < 0.12): hipFollowTau, gaitSmoothingTau, turnToeDegrees.
- Robust winner: min 66.744, mean 71.576, source refined-sweep.
- The robust winner uses stride 0.216 m and plant 1.000. The very short stride and plant saturation show reward exploitation; do not apply v1 winner directly without evaluator regularization.
