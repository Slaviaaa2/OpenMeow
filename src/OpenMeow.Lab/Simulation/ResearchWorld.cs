using OpenMeow.Lab.Domain;
using OpenMeow.Lab.Subjects;

namespace OpenMeow.Lab.Simulation;

public sealed class StaleRevisionException(long expected, long actual)
    : InvalidOperationException($"Stale revision {expected}; current revision is {actual}.")
{
    public long Expected { get; } = expected;
    public long Actual { get; } = actual;
}

internal sealed class HandBody(HandSide side, Vec3 start)
{
    public HandSide Side { get; } = side;
    public Vec3 Position = start;
    public Vec3 Velocity;
    public Vec3 Acceleration;
    public Vec3 PreviousAcceleration;
    public Vec3 Target = start;
    public Vec3 TargetVelocity;
    public bool Grip;
}

internal sealed class SoftPart(BodyPartDefinition definition, Vec3 subjectOffset)
{
    public BodyPartDefinition Definition { get; } = definition;
    public Vec3 RestPosition { get; } = definition.Position + subjectOffset;
    public Vec3 Position = definition.Position + subjectOffset;
    public Vec3 Velocity;
}

internal sealed class MetricAccumulator
{
    private double _forceSum;
    private int _forceSamples;
    private double _targetForceSum;
    private int _targetForceSamples;
    private double _targetForceRelativeErrorSquaredSum;
    private double _targetContactSpeedSum;
    private int _targetContactSpeedSamples;
    private double _peakTargetContactSpeed;
    private double _jerkIntegral;
    private double _peakJerk;
    private double _penetrationIntegral;
    private bool _hadContact;
    private bool _hadTargetContact;
    private Vec3? _lastTargetContactPoint;
    private double? _lastStrokeCoordinate;
    private int _lastStrokeDirection;
    private bool _settlingMeasurementRequested;
    private bool _postContactWindowActive;
    private double _postContactSeconds;
    private double _postContactSpeedSquaredIntegral;
    private double _postContactPeakSubjectSpeed;

    private const double PostContactWindowSeconds = 0.9;

    public double Elapsed;
    public double ContactSeconds;
    public double TargetContactSeconds;
    public double PeakForce;
    public double StrokeTravel;
    public int ContactTransitions;
    public int TargetContactTransitions;

    public void RequestSettlingMeasurement()
    {
        _settlingMeasurementRequested = true;
        _postContactWindowActive = false;
        _postContactSeconds = 0;
        _postContactSpeedSquaredIntegral = 0;
        _postContactPeakSubjectSpeed = 0;
    }

    public void Tick(
        double dt,
        IReadOnlyList<ContactSnapshot> contacts,
        ResearchTaskDefinition task,
        HandBody left,
        HandBody right,
        IReadOnlyList<SoftPart> parts)
    {
        Elapsed += dt;
        bool hasContact = contacts.Count > 0;
        if (hasContact) ContactSeconds += dt;
        if (hasContact != _hadContact) ContactTransitions++;
        _hadContact = hasContact;

        foreach (ContactSnapshot contact in contacts)
        {
            _forceSum += contact.Force;
            _forceSamples++;
            PeakForce = Math.Max(PeakForce, contact.Force);
            _penetrationIntegral += contact.Penetration * dt;
        }

        // A tick can contain contacts from both hands. Target telemetry deliberately
        // chooses one contact (the strongest) so force and path metrics cannot be
        // inflated by duplicate hand samples.
        ContactSnapshot? targetContact = contacts
            .Where(contact => contact.Part.Equals(task.TargetPart, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(contact => contact.Force)
            .FirstOrDefault();
        SoftPart? targetPart = parts.FirstOrDefault(part =>
            part.Definition.Id.Equals(task.TargetPart, StringComparison.OrdinalIgnoreCase));
        bool hasTargetContact = targetContact is not null;
        if (hasTargetContact != _hadTargetContact) TargetContactTransitions++;
        _hadTargetContact = hasTargetContact;
        if (targetContact is ContactSnapshot target)
        {
            TargetContactSeconds += dt;
            _targetForceSum += target.Force;
            _targetForceSamples++;
            double desiredForce = Math.Max(task.DesiredForce, 0.05);
            double relativeForceError = (target.Force - desiredForce) / desiredForce;
            _targetForceRelativeErrorSquaredSum += relativeForceError * relativeForceError;
            PeakTargetForce = Math.Max(PeakTargetForce, target.Force);

            HandBody hand = target.Hand == HandSide.Left ? left : right;
            Vec3 targetVelocity = targetPart?.Velocity ?? Vec3.Zero;
            // Natural stroke speed is motion along the task's declared stroke
            // axis. Full 3D relative speed was dominated by the inward approach
            // and falsely marked even deliberately slow strokes as too fast.
            Vec3 strokeAxis = task.StrokeAxis.Normalized();
            if (strokeAxis.Length < 0.1) strokeAxis = new Vec3(1, 0, 0);
            double contactSpeed = Math.Abs(Vec3.Dot(hand.Velocity - targetVelocity, strokeAxis));
            _targetContactSpeedSum += contactSpeed;
            _targetContactSpeedSamples++;
            _peakTargetContactSpeed = Math.Max(_peakTargetContactSpeed, contactSpeed);

            if (_lastTargetContactPoint is Vec3 previous)
                StrokeTravel += (target.Point - previous).Length;
            _lastTargetContactPoint = target.Point;

            double strokeCoordinate = Vec3.Dot(target.Point, strokeAxis);
            if (_lastStrokeCoordinate is double previousCoordinate)
            {
                double delta = strokeCoordinate - previousCoordinate;
                // Ignore sub-half-millimetre contact jitter when identifying a
                // deliberate stroke reversal.
                if (Math.Abs(delta) > 0.0005)
                {
                    int direction = Math.Sign(delta);
                    if (_lastStrokeDirection != 0 && direction != _lastStrokeDirection)
                        DirectionReversals++;
                    _lastStrokeDirection = direction;
                }
            }
            _lastStrokeCoordinate = strokeCoordinate;
        }
        else
        {
            // Only contiguous target-contact samples form a stroke path.
            _lastTargetContactPoint = null;
            _lastStrokeCoordinate = null;
            _lastStrokeDirection = 0;
            if (_settlingMeasurementRequested)
            {
                _settlingMeasurementRequested = false;
                _postContactWindowActive = true;
            }
        }

        if (_postContactWindowActive)
        {
            if (targetContact is not null)
            {
                // The marked retreat has not cleared contact yet. Wait for a
                // later no-contact tick rather than counting stroke gaps.
                _postContactWindowActive = false;
                _settlingMeasurementRequested = true;
            }
            else
            {
                double sampleSeconds = Math.Min(dt, PostContactWindowSeconds - _postContactSeconds);
                if (sampleSeconds > 0)
                {
                    double subjectSpeedSquared = parts.Count == 0
                        ? 0
                        : parts.Average(part => part.Velocity.Length * part.Velocity.Length);
                    _postContactSpeedSquaredIntegral += subjectSpeedSquared * sampleSeconds;
                    _postContactPeakSubjectSpeed = Math.Max(
                        _postContactPeakSubjectSpeed,
                        Math.Sqrt(subjectSpeedSquared));
                    _postContactSeconds += sampleSeconds;
                }
                if (_postContactSeconds >= PostContactWindowSeconds - 1e-9)
                    _postContactWindowActive = false;
            }
        }

        double handJerk =
            ((left.Acceleration - left.PreviousAcceleration).Length +
             (right.Acceleration - right.PreviousAcceleration).Length) * 0.5;
        _jerkIntegral += handJerk;
        _peakJerk = Math.Max(_peakJerk, handJerk);
    }

    public MetricSnapshot Snapshot() => new(
        ElapsedSeconds: Elapsed,
        ContactSeconds: ContactSeconds,
        TargetContactSeconds: TargetContactSeconds,
        MeanForce: _forceSamples == 0 ? 0 : _forceSum / _forceSamples,
        PeakForce: PeakForce,
        MeanJerk: Elapsed <= 0 ? 0 : _jerkIntegral / Elapsed,
        MeanPenetration: ContactSeconds <= 0 ? 0 : _penetrationIntegral / ContactSeconds,
        StrokeTravel: StrokeTravel,
        ContactTransitions: ContactTransitions,
        MeanTargetForce: _targetForceSamples == 0 ? 0 : _targetForceSum / _targetForceSamples,
        TargetForceRmsRelativeError: _targetForceSamples == 0
            ? 0
            : Math.Sqrt(_targetForceRelativeErrorSquaredSum / _targetForceSamples),
        MeanTargetContactSpeed: _targetContactSpeedSamples == 0
            ? 0
            : _targetContactSpeedSum / _targetContactSpeedSamples,
        PeakTargetContactSpeed: _peakTargetContactSpeed,
        DirectionReversals: DirectionReversals,
        PostContactSampledSeconds: _postContactSeconds,
        PostContactRmsSubjectSpeed: _postContactSeconds <= 0
            ? 0
            : Math.Sqrt(_postContactSpeedSquaredIntegral / _postContactSeconds),
        PostContactPeakSubjectSpeed: _postContactPeakSubjectSpeed,
        PeakTargetForce: PeakTargetForce,
        PeakJerk: _peakJerk,
        TargetContactTransitions: TargetContactTransitions);

    public double PeakTargetForce;
    public int DirectionReversals;
}

public sealed class ResearchWorld
{
    public const double TickSeconds = 1.0 / 90.0;
    private const int MaximumActionTicks = 90 * 10;

    private readonly object _gate = new();
    private readonly ResearchTaskDefinition _task;
    private readonly List<SoftPart> _parts;
    private readonly Dictionary<string, SoftPart> _partsById;
    private readonly HandBody _left = new(HandSide.Left, new(-0.26, 1.38, -0.55));
    private readonly HandBody _right = new(HandSide.Right, new(0.26, 1.38, -0.55));
    private readonly MetricAccumulator _metrics = new();
    private List<ContactSnapshot> _contacts = [];

    public ResearchWorld(
        Guid id,
        string agentId,
        SubjectDefinition subject,
        ResearchTaskDefinition task,
        MotionProfile profile,
        int seed,
        long initialRevision = 0)
    {
        Id = id;
        AgentId = agentId;
        Subject = subject;
        _task = task;
        Profile = profile.Sanitize();
        Seed = seed;
        Revision = initialRevision;
        _parts = subject.Parts.Select(part => new SoftPart(part, task.SubjectOffset)).ToList();
        _partsById = _parts.ToDictionary(part => part.Definition.Id, StringComparer.OrdinalIgnoreCase);
    }

    public Guid Id { get; }
    public string AgentId { get; }
    public SubjectDefinition Subject { get; }
    public int Seed { get; }
    public long Tick { get; private set; }
    public long Revision { get; private set; }
    public MotionProfile Profile { get; private set; }

    public WorldSnapshot Observe()
    {
        lock (_gate)
            return CreateSnapshot();
    }

    public WorldSnapshot SetProfile(MotionProfile profile, long? expectedRevision = null)
    {
        lock (_gate)
        {
            EnsureRevision(expectedRevision);
            Profile = profile.Sanitize();
            Revision++;
            return CreateSnapshot();
        }
    }

    public WorldSnapshot Act(ActionRequest action)
    {
        lock (_gate)
        {
            EnsureRevision(action.ExpectedRevision);
            RunAction(action);
            Revision++;
            return CreateSnapshot();
        }
    }

    public WorldSnapshot RunSequence(SequenceRequest request)
    {
        lock (_gate)
        {
            EnsureRevision(request.ExpectedRevision);
            if (request.Actions.Count is < 1 or > 128)
                throw new ArgumentOutOfRangeException(nameof(request), "A sequence must contain 1-128 actions.");

            // Validate the whole sequence before advancing a single tick. This keeps a
            // rejected request atomic: callers can safely retry with the same revision.
            foreach (ActionRequest action in request.Actions)
                ValidateAction(action);
            foreach (ActionRequest action in request.Actions)
                RunAction(action with { ExpectedRevision = null });
            Revision++;
            return CreateSnapshot();
        }
    }

    public EvaluationResult Evaluate()
    {
        lock (_gate)
            return Evaluator.Evaluate(CreateSnapshot(), _task, _parts);
    }

    private void RunAction(ActionRequest action)
    {
        ValidateAction(action);
        if (action.MeasureSettling)
            _metrics.RequestSettlingMeasurement();
        HandBody hand = action.Hand == HandSide.Left ? _left : _right;
        hand.Grip = action.Grip;
        int ticks = Math.Clamp(
            (int)Math.Ceiling(Math.Clamp(action.DurationSeconds, TickSeconds, 10) / TickSeconds),
            1,
            MaximumActionTicks);

        // Duration describes a trajectory, not how long to wait after an
        // instantaneous target jump. Smoothly move the command from the
        // current physical hand position to the requested endpoint. This lets
        // agents deliberately request a gentle 1.2 s stroke instead of every
        // action becoming the same spring-limited step response.
        Vec3 start = hand.Position;
        Vec3 previousTarget = start;
        for (int i = 0; i < ticks; i++)
        {
            double t = (i + 1.0) / ticks;
            double smoothT = t * t * (3 - 2 * t);
            Vec3 nextTarget = Vec3.Lerp(start, action.Target, smoothT);
            hand.TargetVelocity = (nextTarget - previousTarget) / TickSeconds;
            hand.Target = nextTarget;
            Step();
            previousTarget = nextTarget;
        }
        hand.Target = action.Target;
        hand.TargetVelocity = Vec3.Zero;
    }

    private static void ValidateAction(ActionRequest action)
    {
        if (action.MeasureSettling && action.Grip)
            throw new ArgumentException(
                "A settling measurement must be a non-gripping release/retreat action.");
        if (!double.IsFinite(action.DurationSeconds) ||
            action.DurationSeconds is < TickSeconds or > 10)
            throw new ArgumentOutOfRangeException(
                nameof(action),
                $"Action duration must be between {TickSeconds:R} and 10 seconds.");
        if (!double.IsFinite(action.Target.X + action.Target.Y + action.Target.Z))
            throw new ArgumentException("Action target must be finite.");
        if (action.Target.X is < -8 or > 8 ||
            action.Target.Y is < -2 or > 8 ||
            action.Target.Z is < -8 or > 8)
            throw new ArgumentOutOfRangeException(nameof(action), "Action target is outside the research volume.");
    }

    private void Step()
    {
        StepHand(_left);
        StepHand(_right);
        _contacts = ResolveContacts();
        StepSubject();
        _metrics.Tick(TickSeconds, _contacts, _task, _left, _right, _parts);
        Tick++;
    }

    private void StepHand(HandBody hand)
    {
        hand.PreviousAcceleration = hand.Acceleration;
        double omega = 2 * Math.PI * Profile.PositionSpringHz;
        Vec3 predictedTarget = hand.Target + hand.TargetVelocity * Profile.PredictionSeconds;
        Vec3 acceleration =
            (predictedTarget - hand.Position) * (omega * omega) -
            hand.Velocity * (2 * Profile.DampingRatio * omega);
        hand.Acceleration = acceleration.ClampLength(Profile.MaxAcceleration);
        hand.Velocity = (hand.Velocity + hand.Acceleration * TickSeconds).ClampLength(Profile.MaxSpeed);
        hand.Position += hand.Velocity * TickSeconds;
    }

    private List<ContactSnapshot> ResolveContacts()
    {
        var contacts = new List<ContactSnapshot>(4);
        ResolveHand(_left, contacts);
        ResolveHand(_right, contacts);
        return contacts;
    }

    private void ResolveHand(HandBody hand, List<ContactSnapshot> contacts)
    {
        SoftPart? hit = null;
        Vec3 hitDelta = default;
        double hitDistance = 0;
        double hitPenetration = 0;
        foreach (SoftPart part in _parts)
        {
            Vec3 delta = part.Position - hand.Position;
            double distance = delta.Length;
            double penetration = Profile.HandRadius + part.Definition.Radius - distance;
            if (penetration <= hitPenetration) continue;
            hit = part;
            hitDelta = delta;
            hitDistance = distance;
            hitPenetration = penetration;
        }
        if (hit is null) return;

        Vec3 normal = hitDistance < 1e-7 ? new Vec3(0, 1, 0) : hitDelta / hitDistance;
        double relativeSpeed = Math.Max(0, Vec3.Dot(hand.Velocity - hit.Velocity, normal));
        double stiffness = 12 + 42 * (1 - hit.Definition.Softness);
        double force = hitPenetration * stiffness + relativeSpeed * 1.4;
        if (hand.Grip) force *= 1.18;

        double handYield = 1 - Profile.ContactCompliance;
        hand.Position -= normal * hitPenetration * handYield;
        hand.Velocity -= normal * Math.Max(0, Vec3.Dot(hand.Velocity, normal)) * handYield;
        ApplySubjectImpulse(hit, normal * (force * _task.ReactionGain * TickSeconds));

        Vec3 point = hand.Position + normal * Profile.HandRadius;
        contacts.Add(new ContactSnapshot(
            hand.Side,
            hit.Definition.Id,
            point,
            force,
            hitPenetration));
    }

    private void ApplySubjectImpulse(SoftPart part, Vec3 impulse)
    {
        SoftPart? current = part;
        double transfer = 1;
        int depth = 0;
        while (current is not null && depth++ < 16)
        {
            current.Velocity += impulse * (current.Definition.Mobility * transfer);
            transfer *= 0.42;
            current = current.Definition.Parent is { } parentId &&
                      _partsById.TryGetValue(parentId, out SoftPart? parent)
                ? parent
                : null;
        }
    }

    private void StepSubject()
    {
        foreach (SoftPart part in _parts)
        {
            Vec3 returnTarget = part.Definition.Parent is { } parentId &&
                                _partsById.TryGetValue(parentId, out SoftPart? parent)
                ? parent.Position + (part.RestPosition - parent.RestPosition)
                : part.RestPosition;
            double returnHz = 4.8 - 2.6 * part.Definition.Mobility;
            double omega = 2 * Math.PI * returnHz;
            Vec3 acceleration =
                (returnTarget - part.Position) * (omega * omega) -
                part.Velocity * (2 * 0.82 * omega);
            part.Velocity = (part.Velocity + acceleration * TickSeconds).ClampLength(2.5);
            part.Position += part.Velocity * TickSeconds;

            double maxOffset = 0.025 + 0.17 * part.Definition.Mobility;
            Vec3 offset = part.Position - returnTarget;
            if (offset.Length > maxOffset)
            {
                part.Position = returnTarget + offset.ClampLength(maxOffset);
                part.Velocity *= 0.4;
            }
        }
    }

    private void EnsureRevision(long? expected)
    {
        if (expected.HasValue && expected.Value != Revision)
            throw new StaleRevisionException(expected.Value, Revision);
    }

    private WorldSnapshot CreateSnapshot() => new(
        ExperimentId: Id,
        AgentId: AgentId,
        SubjectId: Subject.Id,
        TaskId: _task.Id,
        Tick: Tick,
        Revision: Revision,
        Profile: Profile,
        Hands:
        [
            new(_left.Side, _left.Position, _left.Velocity, _left.Target, _left.Grip),
            new(_right.Side, _right.Position, _right.Velocity, _right.Target, _right.Grip),
        ],
        Parts: _parts.Select(part => new BodyPartSnapshot(
            part.Definition.Id,
            part.Position,
            part.Definition.Radius,
            part.Definition.Softness)).ToArray(),
        Contacts: _contacts.ToArray(),
        Metrics: _metrics.Snapshot());
}

internal static class Evaluator
{
    public const double Version = 3;

    public static EvaluationResult Evaluate(
        WorldSnapshot snapshot,
        ResearchTaskDefinition task,
        IReadOnlyList<SoftPart> parts)
    {
        MetricSnapshot metrics = snapshot.Metrics;
        double duration = Math.Max(metrics.ElapsedSeconds, 0.001);
        double coverage = Math.Clamp(metrics.TargetContactSeconds / Math.Max(duration * 0.62, 0.001), 0, 1);
        double desiredForce = Math.Max(task.DesiredForce, 0.05);
        double meanTargetForce = metrics.MeanTargetForce;
        double forceError = metrics.TargetForceRmsRelativeError;
        if (forceError <= 0 && metrics.TargetContactSeconds > 0)
            forceError = Math.Abs(meanTargetForce - desiredForce) / desiredForce;
        double forceFit = metrics.TargetContactSeconds <= 0
            ? 0
            : Math.Exp(-forceError * forceError * 2.2);
        double forceRatio = meanTargetForce / desiredForce;
        // This gate is multiplicative at the final score so coverage or stroke
        // cannot compensate for contact that is materially under- or over-forced.
        double forceGate = metrics.TargetContactSeconds <= 0 || meanTargetForce <= 0
            ? 0
            : Math.Exp(-Math.Abs(Math.Log(Math.Clamp(forceRatio, 0.05, 20))) * 1.35);
        double forceComfort = forceFit * forceGate;
        double jerk = metrics.PeakJerk > 0 ? metrics.PeakJerk : metrics.MeanJerk;
        double smoothness = Math.Exp(-jerk / 900);
        double penetration = Math.Exp(-metrics.MeanPenetration / 0.055);
        int continuityTransitions = metrics.TargetContactTransitions > 0
            ? metrics.TargetContactTransitions
            : metrics.ContactTransitions;
        double continuity = Math.Clamp(
            1 - Math.Max(0, continuityTransitions - 2) / Math.Max(duration * 5, 1),
            0,
            1);
        double preferredSpeed = Math.Max(task.PreferredStrokeSpeed, 0.01);
        double speedTolerance = Math.Max(task.StrokeSpeedTolerance, 0.01);
        bool measuredTargetContact = metrics.TargetContactSeconds > 0;
        double speedError = measuredTargetContact
            ? Math.Max(0, Math.Abs(metrics.MeanTargetContactSpeed - preferredSpeed) - speedTolerance) /
              Math.Max(speedTolerance, preferredSpeed * 0.25)
            : double.PositiveInfinity;
        double speedNaturalness = measuredTargetContact
            ? Math.Exp(-speedError * speedError * 1.7)
            : 0;
        double peakSpeedError = Math.Max(0, metrics.PeakTargetContactSpeed - preferredSpeed * 1.8) /
                                Math.Max(preferredSpeed, 0.01);
        speedNaturalness *= Math.Exp(-peakSpeedError * peakSpeedError * 0.8);
        int reversalTarget = Math.Max(task.TargetDirectionReversals, 0);
        double reversalError = reversalTarget == 0
            ? metrics.DirectionReversals
            : Math.Abs(metrics.DirectionReversals - reversalTarget) / (double)Math.Max(reversalTarget, 1);
        double reversalNaturalness = measuredTargetContact
            ? Math.Exp(-reversalError * reversalError * 1.8)
            : 0;
        double stroke = !measuredTargetContact
            ? 0
            : speedNaturalness * reversalNaturalness;

        double settling;
        if (!measuredTargetContact)
        {
            // A quiet subject that was never touched is not evidence of a
            // well-settled interaction, even if a retreat window was marked.
            settling = 0;
        }
        else if (metrics.PostContactSampledSeconds <= 0)
        {
            // No explicit release/retreat was measured. Keep this neutral rather
            // than granting a perfect result from an unobserved settling phase.
            settling = 0.5;
        }
        else
        {
            // Calibrated from the parallel v2/v3 runs: successful releases
            // clustered around 0.0001-0.002 m/s RMS and 0.001-0.006 m/s peak.
            // The old 0.32/0.85 scales rounded nearly every real run to 1.0,
            // so they could not distinguish visible residual motion.
            double rmsSettling = Math.Exp(-metrics.PostContactRmsSubjectSpeed / 0.008);
            double peakSettling = Math.Exp(-metrics.PostContactPeakSubjectSpeed / 0.025);
            double measuredSettling = rmsSettling * 0.7 + peakSettling * 0.3;
            double measurementConfidence = Math.Clamp(metrics.PostContactSampledSeconds / 0.75, 0, 1);
            settling = measurementConfidence * measuredSettling + (1 - measurementConfidence) * 0.5;
        }
        double targetPeakForce = metrics.PeakTargetForce > 0
            ? metrics.PeakTargetForce
            : metrics.PeakForce;
        double peakSafety = Math.Exp(-Math.Max(0, targetPeakForce - desiredForce * 2.5) / 4);

        var components = new Dictionary<string, double>
        {
            ["target_coverage"] = coverage,
            ["force_comfort"] = forceComfort,
            ["smoothness"] = smoothness,
            ["penetration_safety"] = penetration,
            ["contact_continuity"] = continuity,
            ["stroke_naturalness"] = stroke,
            ["reaction_settling"] = settling,
            ["peak_force_safety"] = peakSafety,
            ["target_force_gate"] = forceGate,
            ["target_force_fit"] = forceFit,
            ["target_speed_naturalness"] = speedNaturalness,
            ["direction_reversal_naturalness"] = reversalNaturalness,
            ["post_contact_measurement"] = Math.Clamp(metrics.PostContactSampledSeconds / 0.75, 0, 1),
            ["evaluator_version"] = Version,
        };

        double additiveScore = 100 * (
            coverage * 0.25 +
            forceComfort * 0.18 +
            smoothness * 0.14 +
            penetration * 0.10 +
            continuity * 0.10 +
            stroke * 0.08 +
            settling * 0.10 +
            peakSafety * 0.05);
        double score = additiveScore * forceGate;

        var findings = new List<string>();
        if (metrics.ElapsedSeconds < task.RecommendedDuration * 0.7)
            findings.Add("Run a longer trial so settling and continuity are measurable.");
        if (coverage < 0.55)
            findings.Add("The hand is not maintaining contact with the requested target.");
        if (forceComfort < 0.55)
            findings.Add(meanTargetForce > desiredForce
                ? "Target contact is too firm; increase compliance or reduce approach speed."
                : "Target contact is too light; move slightly deeper or reduce compliance.");
        if (smoothness < 0.6)
            findings.Add("Motion contains abrupt acceleration; lower spring frequency or max acceleration.");
        if (measuredTargetContact && metrics.PostContactSampledSeconds <= 0)
            findings.Add("Release settling was not measured; include a non-gripping retreat window.");
        else if (measuredTargetContact && settling < 0.6)
            findings.Add("The subject keeps oscillating after release; increase damping.");
        if (measuredTargetContact && speedNaturalness < 0.6)
            findings.Add("Stroke speed is outside the preferred natural range for this task.");
        if (measuredTargetContact && reversalNaturalness < 0.6)
            findings.Add("Stroke direction reversals do not match the task's natural rhythm.");
        if (findings.Count == 0)
            findings.Add("Balanced contact, motion and reaction for this simulated task.");

        return new EvaluationResult(
            snapshot.ExperimentId,
            snapshot.Revision,
            Math.Round(score, 3),
            components.ToDictionary(pair => pair.Key, pair => Math.Round(pair.Value, 5)),
            findings,
            snapshot.Profile);
    }
}
