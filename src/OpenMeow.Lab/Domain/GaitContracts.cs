using System.Text.Json.Serialization;

namespace OpenMeow.Lab.Domain;

/// <summary>Whole-body locomotion controls kept separate from hand-contact research.</summary>
public sealed record GaitProfile
{
    public double BodyHeightMeters { get; init; } = 1.65;
    public double HipFollowTau { get; init; } = 0.08;
    public double HipLeanDegrees { get; init; } = 11;
    public double FootSpacingMeters { get; init; } = 0.20;
    public double StrideLengthMeters { get; init; } = 0.45;
    public double StepHeightMeters { get; init; } = 0.06;
    public double GaitSmoothingTau { get; init; } = 0.24;
    public double TurnToeDegrees { get; init; } = 7;
    public double FootPlantStrength { get; init; } = 0.92;

    public GaitProfile Sanitize() => new()
    {
        BodyHeightMeters = Finite(BodyHeightMeters, 1.65, 1.2, 2.2),
        HipFollowTau = Finite(HipFollowTau, 0.08, 0.03, 1.0),
        HipLeanDegrees = Finite(HipLeanDegrees, 11, 0, 25),
        FootSpacingMeters = Finite(FootSpacingMeters, 0.20, 0.10, 0.45),
        StrideLengthMeters = Finite(StrideLengthMeters, 0.45, 0.10, 1.2),
        StepHeightMeters = Finite(StepHeightMeters, 0.06, 0, 0.30),
        GaitSmoothingTau = Finite(GaitSmoothingTau, 0.24, 0.02, 1.0),
        TurnToeDegrees = Finite(TurnToeDegrees, 7, 0, 35),
        FootPlantStrength = Finite(FootPlantStrength, 0.92, 0, 1),
    };

    private static double Finite(double value, double fallback, double minimum, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}

public enum GaitCommand
{
    Idle,
    Forward,
    Strafe,
    TurnInPlace,
    Diagonal,
    Stop,
}

public sealed record GaitScenarioSegment(
    GaitCommand Command,
    double DurationSeconds,
    double SpeedMultiplier = 1)
{
    public GaitScenarioSegment()
        : this(GaitCommand.Idle, 0, 1)
    {
    }

    // Keep the original two-argument constructor for binary consumers.
    public GaitScenarioSegment(GaitCommand command, double durationSeconds)
        : this(command, durationSeconds, 1)
    {
    }

    public GaitScenarioSegment Sanitize() => this with
    {
        DurationSeconds = double.IsFinite(DurationSeconds) ? Math.Clamp(DurationSeconds, 0, 30) : 0,
        SpeedMultiplier = double.IsFinite(SpeedMultiplier) ? Math.Clamp(SpeedMultiplier, .1, 3) : 1,
    };
}

public static class GaitScenarios
{
    public static IReadOnlyList<GaitScenarioSegment> Benchmark { get; } =
    [
        new(GaitCommand.Idle, 1.0),
        new(GaitCommand.Forward, 2.0),
        new(GaitCommand.Strafe, 1.5),
        new(GaitCommand.TurnInPlace, 1.5),
        new(GaitCommand.Diagonal, 1.5),
        new(GaitCommand.Stop, 1.5),
    ];

    public static IReadOnlyList<GaitScenarioSegment> Default => Benchmark;
}

public sealed record GaitBenchmarkRequest
{
    public int Seed { get; init; } = 1;
    public GaitProfile? Profile { get; init; }
    public IReadOnlyList<GaitScenarioSegment>? Scenario { get; init; }
}

public sealed record GaitAutotuneRequest
{
    public int Seed { get; init; } = 1;
    public int Candidates { get; init; } = 24;
    public int Parallelism { get; init; } = 4;
    public GaitProfile? Baseline { get; init; }
}

public readonly record struct GaitPose(Vec3 Position, Vec3 Velocity, double YawDegrees)
{
    [JsonIgnore]
    public bool IsFinite =>
        double.IsFinite(Position.X) && double.IsFinite(Position.Y) && double.IsFinite(Position.Z) &&
        double.IsFinite(Velocity.X) && double.IsFinite(Velocity.Y) && double.IsFinite(Velocity.Z) &&
        double.IsFinite(YawDegrees);
}

public sealed record GaitSample(
    double TimeSeconds,
    GaitCommand Command,
    GaitPose Waist,
    GaitPose LeftFoot,
    GaitPose RightFoot,
    bool LeftPlanted,
    bool RightPlanted,
    double LeftPhase,
    double RightPhase)
{
    [JsonIgnore]
    public bool IsFinite =>
        double.IsFinite(TimeSeconds) && Waist.IsFinite && LeftFoot.IsFinite && RightFoot.IsFinite &&
        double.IsFinite(LeftPhase) && double.IsFinite(RightPhase);
}

public sealed record GaitMetrics(
    double ElapsedSeconds = 0,
    double PlantedFootWorldSlipMetersPerSecond = 0,
    double PlantedHeightErrorMeters = 0,
    double SwingClearanceMeters = 0,
    double WaistPeakAcceleration = 0,
    double WaistPeakJerk = 0,
    double LeftRightPhaseAsymmetry = 0,
    double StopSettlingSpeed = 0,
    double StopOvershootMeters = 0,
    double ToeTurnAlignmentDegrees = 0,
    int NonFinitePoseCount = 0,
    int SwingSteps = 0,
    double CommandedTravelMeters = 0,
    double ActualTravelMeters = 0,
    double TurnDegrees = 0,
    double MovementSeconds = 0)
{
    [JsonIgnore] public double PlantedSlip => PlantedFootWorldSlipMetersPerSecond;
    [JsonIgnore] public double PlantedHeightError => PlantedHeightErrorMeters;
    [JsonIgnore] public double SwingClearance => SwingClearanceMeters;
    [JsonIgnore] public double WaistPeakAccel => WaistPeakAcceleration;
    [JsonIgnore] public double PhaseAsymmetry => LeftRightPhaseAsymmetry;
    [JsonIgnore] public double StopSettling => StopSettlingSpeed;
    [JsonIgnore] public double StopOvershoot => StopOvershootMeters;
    [JsonIgnore] public double ToeTurnAlignment => ToeTurnAlignmentDegrees;
    [JsonIgnore] public double MovementDurationSeconds => MovementSeconds;
    [JsonIgnore] public double PoseFiniteness => NonFinitePoseCount == 0 ? 1 : 0;
};

public sealed record GaitEvaluationResult(
    double Score,
    IReadOnlyDictionary<string, double> Components,
    IReadOnlyList<string> Findings,
    GaitProfile Profile,
    GaitMetrics Metrics);

public sealed record GaitBenchmarkResult(
    GaitEvaluationResult Evaluation,
    IReadOnlyList<GaitSample> Samples,
    GaitProfile Profile,
    GaitMetrics Metrics)
{
    public double Score => Evaluation.Score;
    public IReadOnlyDictionary<string, double> Components => Evaluation.Components;
    public IReadOnlyList<string> Findings => Evaluation.Findings;
}

public sealed record GaitTuneResult(
    GaitProfile BestProfile,
    double BestScore,
    int CandidatesEvaluated,
    IReadOnlyList<GaitEvaluationResult> TopResults);
