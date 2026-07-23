using System.Text.Json.Serialization;

namespace OpenMeow.Lab.Domain;

public readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);

    [JsonIgnore]
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 value, double scale) => new(value.X * scale, value.Y * scale, value.Z * scale);
    public static Vec3 operator /(Vec3 value, double scale) => scale == 0 ? Zero : value * (1.0 / scale);

    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vec3 Lerp(Vec3 a, Vec3 b, double t) => a + (b - a) * Math.Clamp(t, 0, 1);
    public Vec3 Normalized() => Length < 1e-9 ? Zero : this / Length;
    public Vec3 ClampLength(double maximum) => Length <= maximum ? this : Normalized() * maximum;
}

public enum HandSide
{
    Left,
    Right,
}

public sealed record MotionProfile
{
    public string Name { get; init; } = "balanced";
    public double PositionSpringHz { get; init; } = 7.5;
    public double DampingRatio { get; init; } = 0.92;
    public double MaxSpeed { get; init; } = 1.8;
    public double MaxAcceleration { get; init; } = 12.0;
    public double ContactCompliance { get; init; } = 0.62;
    public double PredictionSeconds { get; init; } = 0.035;
    public double HandRadius { get; init; } = 0.075;
    public IReadOnlyDictionary<string, string> Bindings { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["look"] = "Mouse",
            ["right_hand_move"] = "LeftShift+Mouse",
            ["right_arm_tilt"] = "LeftShift+MiddleMouse",
            ["left_hand_move"] = "LeftCtrl+Mouse",
            ["left_arm_tilt"] = "LeftCtrl+MiddleMouse",
            ["grip"] = "RightMouse",
            ["touch"] = "LeftMouse",
        };

    public MotionProfile Sanitize() => this with
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "unnamed" : Name.Trim(),
        PositionSpringHz = Math.Clamp(PositionSpringHz, 1, 20),
        DampingRatio = Math.Clamp(DampingRatio, 0.2, 2),
        MaxSpeed = Math.Clamp(MaxSpeed, 0.1, 6),
        MaxAcceleration = Math.Clamp(MaxAcceleration, 0.5, 50),
        ContactCompliance = Math.Clamp(ContactCompliance, 0, 1),
        PredictionSeconds = Math.Clamp(PredictionSeconds, 0, 0.2),
        HandRadius = Math.Clamp(HandRadius, 0.025, 0.15),
    };
}

public sealed record BodyPartDefinition
{
    public required string Id { get; init; }
    public required Vec3 Position { get; init; }
    public double Radius { get; init; } = 0.1;
    public double Mobility { get; init; } = 0.25;
    public double Softness { get; init; } = 0.5;
    public string? Parent { get; init; }
}

public sealed record SubjectDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string Description { get; init; } = "";
    public IReadOnlyList<BodyPartDefinition> Parts { get; init; } = [];
}

public sealed record ResearchTaskDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required string TargetPart { get; init; }
    public Vec3 SubjectOffset { get; init; }
    public Vec3 ApproachPoint { get; init; }
    public Vec3 StrokeAxis { get; init; } = new(1, 0, 0);
    public double DesiredForce { get; init; } = 2.2;
    public double PreferredStrokeSpeed { get; init; } = 0.18;
    public double StrokeSpeedTolerance { get; init; } = 0.12;
    public int TargetDirectionReversals { get; init; } = 4;
    public double RecommendedDuration { get; init; } = 2.5;
    public double ReactionGain { get; init; } = 1;
}

public sealed record CreateExperimentRequest
{
    public string SubjectId { get; init; } = "default_mew";
    public string TaskId { get; init; } = "head_petting";
    public string AgentId { get; init; } = "anonymous";
    public int Seed { get; init; } = 1;
    public MotionProfile? Profile { get; init; }
}

public sealed record ActionRequest
{
    public long? ExpectedRevision { get; init; }
    public HandSide Hand { get; init; } = HandSide.Right;
    public required Vec3 Target { get; init; }
    public double DurationSeconds { get; init; } = 0.5;
    public bool Grip { get; init; }
    /// <summary>
    /// Starts a fresh, explicit post-contact settling measurement once this
    /// action has left the task target. Ordinary stroke gaps are not measured.
    /// </summary>
    public bool MeasureSettling { get; init; }
    public string Label { get; init; } = "move";
}

public sealed record SequenceRequest
{
    public long? ExpectedRevision { get; init; }
    public IReadOnlyList<ActionRequest> Actions { get; init; } = [];
}

public sealed record HandSnapshot(
    HandSide Side,
    Vec3 Position,
    Vec3 Velocity,
    Vec3 Target,
    bool Grip);

public sealed record BodyPartSnapshot(
    string Id,
    Vec3 Position,
    double Radius,
    double Softness);

public sealed record ContactSnapshot(
    HandSide Hand,
    string Part,
    Vec3 Point,
    double Force,
    double Penetration);

public sealed record MetricSnapshot(
    double ElapsedSeconds,
    double ContactSeconds,
    double TargetContactSeconds,
    double MeanForce,
    double PeakForce,
    double MeanJerk,
    double MeanPenetration,
    double StrokeTravel,
    int ContactTransitions,
    double MeanTargetForce = 0,
    double TargetForceRmsRelativeError = 0,
    double MeanTargetContactSpeed = 0,
    double PeakTargetContactSpeed = 0,
    int DirectionReversals = 0,
    double PostContactSampledSeconds = 0,
    double PostContactRmsSubjectSpeed = 0,
    double PostContactPeakSubjectSpeed = 0,
    double PeakTargetForce = 0,
    double PeakJerk = 0,
    int TargetContactTransitions = 0)
{
    // Preserve the original nine-argument constructor for source and binary
    // callers while exposing the v2 telemetry as additive fields.
    public MetricSnapshot(
        double elapsedSeconds,
        double contactSeconds,
        double targetContactSeconds,
        double meanForce,
        double peakForce,
        double meanJerk,
        double meanPenetration,
        double strokeTravel,
        int contactTransitions)
        : this(
            elapsedSeconds,
            contactSeconds,
            targetContactSeconds,
            meanForce,
            peakForce,
            meanJerk,
            meanPenetration,
            strokeTravel,
            contactTransitions,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0)
    {
    }
}

public sealed record WorldSnapshot(
    Guid ExperimentId,
    string AgentId,
    string SubjectId,
    string TaskId,
    long Tick,
    long Revision,
    MotionProfile Profile,
    IReadOnlyList<HandSnapshot> Hands,
    IReadOnlyList<BodyPartSnapshot> Parts,
    IReadOnlyList<ContactSnapshot> Contacts,
    MetricSnapshot Metrics);

public sealed record EvaluationResult(
    Guid ExperimentId,
    long Revision,
    double Score,
    IReadOnlyDictionary<string, double> Components,
    IReadOnlyList<string> Findings,
    MotionProfile Profile);

public sealed record ComparisonResult(
    Guid Winner,
    IReadOnlyList<EvaluationResult> Ranking,
    string Summary);

public sealed record TuneRequest
{
    public string TaskId { get; init; } = "head_petting";
    public string SubjectId { get; init; } = "default_mew";
    public int Seed { get; init; } = 1;
    public int Candidates { get; init; } = 24;
    public int Parallelism { get; init; } = 4;
    public MotionProfile? Baseline { get; init; }
}

public sealed record TuneResult(
    MotionProfile BestProfile,
    double BestScore,
    int CandidatesEvaluated,
    IReadOnlyList<EvaluationResult> TopResults);
