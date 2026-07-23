using System.Collections.Concurrent;
using OpenMeow.Lab.Domain;
using OpenMeow.Lab.Simulation;

namespace OpenMeow.Lab.Orchestration;

/// <summary>Stateless gait benchmark and evaluator entry points.</summary>
public static class GaitResearch
{
    public static GaitBenchmarkResult RunBenchmark(
        GaitBenchmarkRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        request ??= new GaitBenchmarkRequest();
        GaitProfile profile = (request.Profile ?? new GaitProfile()).Sanitize();
        var simulator = new GaitSimulator(profile, request.Seed);
        IReadOnlyList<GaitSample> samples = simulator.Run(request.Scenario, cancellationToken);
        GaitMetrics metrics = simulator.Metrics;
        GaitEvaluationResult evaluation = GaitEvaluator.Evaluate(profile, metrics);
        return new GaitBenchmarkResult(evaluation, samples, profile, metrics);
    }

    public static GaitEvaluationResult Evaluate(GaitBenchmarkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return GaitEvaluator.Evaluate(result.Profile, result.Metrics);
    }

    public static Task<GaitTuneResult> AutoTuneAsync(
        GaitAutotuneRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        request ??= new GaitAutotuneRequest();
        int count = Math.Clamp(request.Candidates, 2, 128);
        int parallelism = Math.Clamp(request.Parallelism, 1, 16);
        GaitProfile baseline = (request.Baseline ?? new GaitProfile()).Sanitize();
        GaitProfile[] candidates = CreateCandidates(baseline, request.Seed, count);
        var results = new ConcurrentBag<GaitEvaluationResult>();
        return RunAsync();

        async Task<GaitTuneResult> RunAsync()
        {
            await Parallel.ForEachAsync(
                candidates,
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                (profile, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    results.Add(RunBenchmark(
                        new GaitBenchmarkRequest { Seed = request.Seed, Profile = profile },
                        token).Evaluation);
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);

            GaitEvaluationResult[] ranking = results
                .OrderByDescending(item => item.Score)
                .ThenBy(item => ProfileKey(item.Profile), StringComparer.Ordinal)
                .ToArray();
            if (ranking.Length == 0)
                throw new InvalidOperationException("Gait autotuning produced no result.");
            return new GaitTuneResult(ranking[0].Profile, ranking[0].Score, ranking.Length, ranking.Take(5).ToArray());
        }
    }

    private static GaitProfile[] CreateCandidates(GaitProfile baseline, int seed, int count)
    {
        var random = new Random(seed);
        var candidates = new GaitProfile[count];
        candidates[0] = baseline with { };
        for (int i = 1; i < count; i++)
        {
            double Scale(double range) => Math.Exp((random.NextDouble() * 2 - 1) * range);
            candidates[i] = (baseline with
            {
                HipFollowTau = baseline.HipFollowTau * Scale(.45),
                HipLeanDegrees = baseline.HipLeanDegrees + (random.NextDouble() * 2 - 1) * 5,
                StrideLengthMeters = baseline.StrideLengthMeters * Scale(.38),
                StepHeightMeters = baseline.StepHeightMeters * Scale(.5),
                GaitSmoothingTau = baseline.GaitSmoothingTau * Scale(.45),
                TurnToeDegrees = baseline.TurnToeDegrees + (random.NextDouble() * 2 - 1) * 10,
                FootPlantStrength = Math.Clamp(baseline.FootPlantStrength + (random.NextDouble() * 2 - 1) * .16, 0, 1),
            }).Sanitize();
        }
        return candidates;
    }

    private static string ProfileKey(GaitProfile profile) =>
        string.Join("|", profile.BodyHeightMeters.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            profile.HipFollowTau.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            profile.HipLeanDegrees.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            profile.FootSpacingMeters.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            profile.StrideLengthMeters.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            profile.StepHeightMeters.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            profile.GaitSmoothingTau.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            profile.TurnToeDegrees.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            profile.FootPlantStrength.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
}

public static class GaitEvaluator
{
    public const double Version = 8;

    public static GaitEvaluationResult Evaluate(GaitMetrics metrics, GaitProfile profile) =>
        Evaluate(profile, metrics);

    public static GaitEvaluationResult Evaluate(GaitProfile profile, GaitMetrics metrics)
    {
        profile = profile.Sanitize();
        bool metricsFinite = IsFinite(metrics);
        GaitMetrics safeMetrics = SanitizeMetrics(metrics);
        metrics = safeMetrics;
        double slip = Math.Exp(-Math.Max(0, metrics.PlantedFootWorldSlipMetersPerSecond) / .05);
        double height = Math.Exp(-Math.Max(0, metrics.PlantedHeightErrorMeters) / .025);
        double clearance = metrics.SwingSteps <= 0
            ? 0
            : Math.Clamp(metrics.SwingClearanceMeters / Math.Max(profile.StepHeightMeters * .55, .01), 0, 1) *
              Math.Exp(-Math.Max(0, metrics.SwingClearanceMeters - profile.StepHeightMeters * 1.8) / .15);
        double smoothness = Math.Exp(-Math.Max(0, metrics.WaistPeakAcceleration) / 14) *
                            Math.Exp(-Math.Max(0, metrics.WaistPeakJerk) / 180);
        double phase = Math.Exp(-Math.Max(0, metrics.LeftRightPhaseAsymmetry) * 6);
        double settling = metrics.StopSettlingSpeed <= 0 && metrics.StopOvershootMeters <= 0
            ? .5
            : Math.Exp(-Math.Max(0, metrics.StopSettlingSpeed) / .06) *
              Math.Exp(-Math.Max(0, metrics.StopOvershootMeters) / .35);
        double toe = metrics.TurnDegrees < 10
            ? 0
            : Math.Exp(-Math.Max(0, metrics.ToeTurnAlignmentDegrees) / 12);
        double movement = metrics.CommandedTravelMeters >= .25
            ? Math.Clamp(metrics.ActualTravelMeters / Math.Max(metrics.CommandedTravelMeters, .01), 0, 1)
            : metrics.TurnDegrees >= 10
                ? 1
                : 0;
        double stepping = Math.Clamp(metrics.SwingSteps / 4d, 0, 1);
        double cadence = metrics.MovementSeconds <= 0 || !double.IsFinite(metrics.MovementSeconds)
            ? 0
            : Gaussian(metrics.SwingSteps / metrics.MovementSeconds, 1.8, .9);
        double anthropometric = (
            Gaussian(profile.StrideLengthMeters, profile.BodyHeightMeters * .30, .14) +
            Gaussian(profile.StepHeightMeters, profile.BodyHeightMeters * .045, .04) +
            Gaussian(profile.FootSpacingMeters, profile.BodyHeightMeters * .12, .06) +
            Gaussian(profile.HipLeanDegrees, 8, 8) +
            Gaussian(profile.TurnToeDegrees, 10, 12)) / 5;
        double plantCompliance = Gaussian(profile.FootPlantStrength, .90, .07);
        double finite = metricsFinite && metrics.NonFinitePoseCount == 0 ? 1 : 0;
        var components = new Dictionary<string, double>
        {
            ["planted_slip"] = Clamp01(slip),
            ["planted_height"] = Clamp01(height),
            ["swing_clearance"] = Clamp01(clearance),
            ["waist_smoothness"] = Clamp01(smoothness),
            ["phase_symmetry"] = Clamp01(phase),
            ["stop_settling"] = Clamp01(settling),
            ["toe_turn_alignment"] = Clamp01(toe),
            ["movement_coverage"] = Clamp01(movement),
            ["stepping_coverage"] = Clamp01(stepping),
            ["cadence_naturalness"] = Clamp01(cadence),
            ["anthropometric_shape"] = Clamp01(anthropometric),
            ["plant_compliance"] = Clamp01(plantCompliance),
            ["pose_finiteness"] = finite,
            ["evaluator_version"] = Version,
        };

        double weighted = 100 * (
            slip * .08 + height * .05 + clearance * .09 + smoothness * .12 + phase * .08 +
            settling * .12 + toe * .08 + movement * .08 + stepping * .05 + cadence * .10 +
            anthropometric * .10 + plantCompliance * .05);
        // A stationary or non-finite run is useful diagnostically, but is not a
        // successful gait regardless of how quiet its feet appear.
        double score = !metricsFinite ? 0 : weighted * finite * Math.Min(movement, stepping);
        var findings = new List<string>();
        if (!metricsFinite || metrics.NonFinitePoseCount > 0) findings.Add("Pose output contains non-finite values.");
        if (movement < .55) findings.Add("Commanded movement was not reproduced; check hip follow and smoothing.");
        if (stepping < .55) findings.Add("The run did not produce enough alternating swing steps.");
        if (slip < .7) findings.Add("Planted feet slip in world space; increase foot plant strength.");
        if (height < .75) findings.Add("Planted feet are leaving the floor; reduce stance drift.");
        if (clearance < .7) findings.Add("Swing clearance is too low for reliable foot separation.");
        if (smoothness < .65) findings.Add("Waist acceleration or jerk is abrupt; increase gait smoothing.");
        if (phase < .75) findings.Add("Left/right gait phases are asymmetric.");
        if (settling < .65) findings.Add("Stop leaves residual speed or overshoot.");
        if (toe < .7) findings.Add("Turn-in-place toe yaw does not follow the turn direction.");
        if (cadence < .65) findings.Add("Step cadence is outside the natural 1.8 steps/second band.");
        if (anthropometric < .65) findings.Add("Stride, clearance, spacing or posture is outside the anthropometric shape prior.");
        if (plantCompliance < .65) findings.Add("Foot planting is over-constrained or too compliant for a natural gait.");
        if (findings.Count == 0) findings.Add("Whole-body gait remains planted, alternating and finite across the benchmark.");

        return new GaitEvaluationResult(Math.Round(Math.Clamp(score, 0, 100), 3),
            components.ToDictionary(pair => pair.Key, pair => pair.Key == "evaluator_version"
                ? Version
                : Math.Round(Clamp01(pair.Value), 5)), findings, profile, metrics);
    }

    private static bool IsFinite(GaitMetrics metrics) =>
        double.IsFinite(metrics.ElapsedSeconds) && double.IsFinite(metrics.PlantedFootWorldSlipMetersPerSecond) &&
        double.IsFinite(metrics.PlantedHeightErrorMeters) && double.IsFinite(metrics.SwingClearanceMeters) &&
        double.IsFinite(metrics.WaistPeakAcceleration) && double.IsFinite(metrics.WaistPeakJerk) &&
        double.IsFinite(metrics.LeftRightPhaseAsymmetry) && double.IsFinite(metrics.StopSettlingSpeed) &&
        double.IsFinite(metrics.StopOvershootMeters) && double.IsFinite(metrics.ToeTurnAlignmentDegrees) &&
        double.IsFinite(metrics.CommandedTravelMeters) && double.IsFinite(metrics.ActualTravelMeters) &&
        double.IsFinite(metrics.TurnDegrees) && double.IsFinite(metrics.MovementSeconds) &&
        metrics.ElapsedSeconds >= 0 && metrics.PlantedFootWorldSlipMetersPerSecond >= 0 &&
        metrics.PlantedHeightErrorMeters >= 0 && metrics.SwingClearanceMeters >= 0 &&
        metrics.WaistPeakAcceleration >= 0 && metrics.WaistPeakJerk >= 0 &&
        metrics.LeftRightPhaseAsymmetry >= 0 && metrics.StopSettlingSpeed >= 0 &&
        metrics.StopOvershootMeters >= 0 && metrics.ToeTurnAlignmentDegrees >= 0 &&
        metrics.NonFinitePoseCount >= 0 && metrics.SwingSteps >= 0 &&
        metrics.CommandedTravelMeters >= 0 && metrics.ActualTravelMeters >= 0 &&
        metrics.TurnDegrees >= 0 && metrics.MovementSeconds >= 0;

    private static GaitMetrics SanitizeMetrics(GaitMetrics metrics) => metrics with
    {
        ElapsedSeconds = NonNegativeFiniteOrZero(metrics.ElapsedSeconds),
        PlantedFootWorldSlipMetersPerSecond = NonNegativeFiniteOrZero(metrics.PlantedFootWorldSlipMetersPerSecond),
        PlantedHeightErrorMeters = NonNegativeFiniteOrZero(metrics.PlantedHeightErrorMeters),
        SwingClearanceMeters = NonNegativeFiniteOrZero(metrics.SwingClearanceMeters),
        WaistPeakAcceleration = NonNegativeFiniteOrZero(metrics.WaistPeakAcceleration),
        WaistPeakJerk = NonNegativeFiniteOrZero(metrics.WaistPeakJerk),
        LeftRightPhaseAsymmetry = NonNegativeFiniteOrZero(metrics.LeftRightPhaseAsymmetry),
        StopSettlingSpeed = NonNegativeFiniteOrZero(metrics.StopSettlingSpeed),
        StopOvershootMeters = NonNegativeFiniteOrZero(metrics.StopOvershootMeters),
        ToeTurnAlignmentDegrees = NonNegativeFiniteOrZero(metrics.ToeTurnAlignmentDegrees),
        NonFinitePoseCount = Math.Max(0, metrics.NonFinitePoseCount),
        SwingSteps = Math.Max(0, metrics.SwingSteps),
        CommandedTravelMeters = NonNegativeFiniteOrZero(metrics.CommandedTravelMeters),
        ActualTravelMeters = NonNegativeFiniteOrZero(metrics.ActualTravelMeters),
        TurnDegrees = NonNegativeFiniteOrZero(metrics.TurnDegrees),
        MovementSeconds = NonNegativeFiniteOrZero(metrics.MovementSeconds),
    };

    private static double NonNegativeFiniteOrZero(double value) =>
        double.IsFinite(value) ? Math.Max(0, value) : 0;

    private static double Clamp01(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    private static double Gaussian(double value, double target, double sigma)
    {
        if (!double.IsFinite(value) || !double.IsFinite(target) || !double.IsFinite(sigma) || sigma <= 0)
            return 0;
        double z = (value - target) / sigma;
        return Math.Exp(-.5 * z * z);
    }
}
