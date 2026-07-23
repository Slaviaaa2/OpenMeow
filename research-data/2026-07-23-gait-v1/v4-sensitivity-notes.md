# Gait evaluator v4 final analysis

- 2,048 v4 autotune candidates across two independent baselines.
- 77 unique top profiles cross-validated on default, stop-heavy, turn-heavy, transitions, and endurance conditions.
- Robust maximin winner: min 64.627, mean 68.6718.
- Winner profile: {"bodyHeightMeters":1.65,"hipFollowTau":0.14,"hipLeanDegrees":8,"footSpacingMeters":0.2,"strideLengthMeters":0.5,"stepHeightMeters":0.07,"gaitSmoothingTau":0.14,"turnToeDegrees":10,"footPlantStrength":0.9}.
- The exact balanced baseline won, so the adopted rounded driver values need no additional rounding error.
- Body height and foot spacing were fixed calibration values and their correlations are not identifiable by design.
- No driver-profile apply endpoint was called during research.
