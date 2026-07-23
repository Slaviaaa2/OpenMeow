using OpenMeow;
using OpenMeow.Lab.Domain;

namespace OpenMeow.Lab.Simulation;

/// <summary>
/// Deterministic gait benchmark adapter backed by the same body gait kernel used
/// by the desktop driver. The benchmark supplies a synthetic HMD trace; it does
/// not contain a second foot-placement implementation.
/// </summary>
public sealed class GaitSimulator
{
    public const double TickSeconds = 1.0 / 90.0;
    private const double WalkSpeed = .9;
    private const double StrafeSpeed = .9;
    private const double TurnSpeedDegrees = 60;
    private const double InputSmoothingTau = .18;

    private readonly GaitProfile _profile;
    private readonly BodyGaitKernel _kernel = new();
    private BodyGaitHeadPose _head;
    private Vec3 _smoothedVelocity;
    private double _smoothedYawRate;
    private GaitCommand _command;
    private GaitCommand _previousCommand;
    private double _time;
    private Vec3 _lastWaistPosition;
    private Vec3 _lastWaistVelocity;
    private Vec3 _lastWaistAcceleration;
    private bool _hasPreviousFrame;
    private bool _leftWasSwing;
    private bool _rightWasSwing;
    private double _leftStepSeconds;
    private double _rightStepSeconds;
    private int _leftCompletedSteps;
    private int _rightCompletedSteps;
    private double _leftStepTime;
    private double _rightStepTime;
    private double _peakClearance;
    private double _peakAcceleration;
    private double _peakJerk;
    private double _slipSum;
    private double _plantWeightSum;
    private double _heightErrorSum;
    private double _stopSpeedSum;
    private int _stopSamples;
    private double _stopElapsed;
    private Vec3 _stopOrigin;
    private double _stopOvershoot;
    private double _toeErrorSum;
    private int _toeSamples;
    private double _turnElapsed;
    private double _commandedTravel;
    private double _actualTravel;
    private double _turnDegrees;
    private double _movementSeconds;
    private int _swingSteps;
    private int _nonFinitePoseCount;

    public GaitSimulator(GaitProfile? profile = null, int seed = 1)
    {
        // Seed is accepted for parity with the other Lab simulators. The trace
        // and shared kernel are deterministic and do not use randomness.
        _ = seed;
        _profile = (profile ?? new GaitProfile()).Sanitize();
        Reset();
    }

    public GaitProfile Profile => _profile;
    public double TimeSeconds => _time;
    public GaitCommand Command => _command;
    public GaitMetrics Metrics => BuildMetrics();

    public void Reset()
    {
        _head = new BodyGaitHeadPose(0, _profile.BodyHeightMeters, 0, 0);
        _smoothedVelocity = Vec3.Zero;
        _smoothedYawRate = 0;
        _command = GaitCommand.Idle;
        _previousCommand = GaitCommand.Idle;
        _time = 0;
        _lastWaistPosition = Vec3.Zero;
        _lastWaistVelocity = Vec3.Zero;
        _lastWaistAcceleration = Vec3.Zero;
        _hasPreviousFrame = false;
        _leftWasSwing = false;
        _rightWasSwing = false;
        _leftStepSeconds = 0;
        _rightStepSeconds = 0;
        _leftCompletedSteps = 0;
        _rightCompletedSteps = 0;
        _leftStepTime = 0;
        _rightStepTime = 0;
        _peakClearance = 0;
        _peakAcceleration = 0;
        _peakJerk = 0;
        _slipSum = 0;
        _plantWeightSum = 0;
        _heightErrorSum = 0;
        _stopSpeedSum = 0;
        _stopSamples = 0;
        _stopElapsed = 0;
        _stopOrigin = Vec3.Zero;
        _stopOvershoot = 0;
        _toeErrorSum = 0;
        _toeSamples = 0;
        _turnElapsed = 0;
        _commandedTravel = 0;
        _actualTravel = 0;
        _turnDegrees = 0;
        _movementSeconds = 0;
        _swingSteps = 0;
        _nonFinitePoseCount = 0;
        _kernel.Configure(ToParameters(_profile));
        _kernel.Reset(_head);
    }

    public GaitSample Step(GaitCommand command, double dt = TickSeconds)
        => Step(command, dt, 1);

    public GaitSample Step(GaitCommand command, double dt, double speedMultiplier)
    {
        if (!double.IsFinite(dt) || dt <= 0) dt = TickSeconds;
        dt = Math.Clamp(dt, 1e-5, 0.1);
        speedMultiplier = double.IsFinite(speedMultiplier) ? Math.Clamp(speedMultiplier, .1, 3) : 1;
        _command = command;

        Vec3 targetVelocity = DesiredWorldVelocity(command, _head.Yaw) * speedMultiplier;
        double targetYawRate = command == GaitCommand.TurnInPlace
            ? TurnSpeedDegrees * speedMultiplier * Math.PI / 180.0
            : 0;
        double inputAlpha = Math.Clamp(dt / InputSmoothingTau, 0, 1);
        _smoothedVelocity = Vec3.Lerp(_smoothedVelocity, targetVelocity, inputAlpha);
        _smoothedYawRate += (targetYawRate - _smoothedYawRate) * inputAlpha;
        _smoothedVelocity = Finite(_smoothedVelocity);
        _smoothedYawRate = Finite(_smoothedYawRate);

        double previousHeadYaw = _head.Yaw;
        _head = new BodyGaitHeadPose(
            _head.X + _smoothedVelocity.X * dt,
            _profile.BodyHeightMeters,
            _head.Z + _smoothedVelocity.Z * dt,
            WrapRadians(_head.Yaw + _smoothedYawRate * dt));
        _commandedTravel += Math.Sqrt(targetVelocity.X * targetVelocity.X + targetVelocity.Z * targetVelocity.Z) * dt;
        if (command is GaitCommand.Forward or GaitCommand.Strafe or GaitCommand.Diagonal or GaitCommand.TurnInPlace)
        {
            _movementSeconds += dt;
        }
        _turnDegrees += Math.Abs(WrapRadians(_head.Yaw - previousHeadYaw)) * 180 / Math.PI;

        BodyGaitFrame frame = _kernel.Step(dt, _head);
        GaitPose waist = ToPose(frame.Waist);
        GaitPose left = ToPose(frame.LeftFoot);
        GaitPose right = ToPose(frame.RightFoot);

        Vec3 waistVelocity = new(frame.Waist.Vx, frame.Waist.Vy, frame.Waist.Vz);
        Vec3 acceleration = _hasPreviousFrame
            ? (waistVelocity - _lastWaistVelocity) / dt
            : Vec3.Zero;
        Vec3 jerk = _hasPreviousFrame
            ? (acceleration - _lastWaistAcceleration) / dt
            : Vec3.Zero;
        Vec3 waistGround = new(frame.Waist.X, 0, frame.Waist.Z);
        _actualTravel += _hasPreviousFrame
            ? (waistGround - _lastWaistPosition).Length
            : 0;
        _lastWaistPosition = waistGround;
        _lastWaistVelocity = waistVelocity;
        _lastWaistAcceleration = acceleration;
        _hasPreviousFrame = true;

        bool leftSwing = !frame.LeftPlanted;
        bool rightSwing = !frame.RightPlanted;
        if (leftSwing && !_leftWasSwing)
        {
            _swingSteps++;
            _leftStepTime = 0;
        }
        if (rightSwing && !_rightWasSwing)
        {
            _swingSteps++;
            _rightStepTime = 0;
        }
        if (leftSwing) _leftStepTime += dt;
        if (rightSwing) _rightStepTime += dt;
        if (!leftSwing && _leftWasSwing && _leftStepTime > 0)
        {
            _leftStepSeconds += _leftStepTime;
            _leftCompletedSteps++;
        }
        if (!rightSwing && _rightWasSwing && _rightStepTime > 0)
        {
            _rightStepSeconds += _rightStepTime;
            _rightCompletedSteps++;
        }
        _leftWasSwing = leftSwing;
        _rightWasSwing = rightSwing;

        double floorY = _head.Y - _profile.BodyHeightMeters + .025;
        if (frame.LeftPlanted || frame.RightPlanted)
        {
            if (frame.LeftPlanted)
            {
                double weight = PlantContactWeight(frame.Phase);
                _slipSum += HorizontalSpeed(frame.LeftFoot) * weight;
                _heightErrorSum += Math.Abs(frame.LeftFoot.Y - floorY) * weight;
                _plantWeightSum += weight;
            }
            if (frame.RightPlanted)
            {
                double weight = PlantContactWeight(frame.Phase + Math.PI);
                _slipSum += HorizontalSpeed(frame.RightFoot) * weight;
                _heightErrorSum += Math.Abs(frame.RightFoot.Y - floorY) * weight;
                _plantWeightSum += weight;
            }
        }
        if (leftSwing) _peakClearance = Math.Max(_peakClearance, frame.LeftFoot.Y - floorY);
        if (rightSwing) _peakClearance = Math.Max(_peakClearance, frame.RightFoot.Y - floorY);
        _peakAcceleration = Math.Max(_peakAcceleration, acceleration.Length);
        _peakJerk = Math.Max(_peakJerk, jerk.Length);
        if (command == GaitCommand.Stop)
        {
            if (_previousCommand != GaitCommand.Stop)
            {
                _stopOrigin = waistGround;
                _stopElapsed = 0;
            }
            _stopElapsed += dt;
            // The first half of a stop segment is deliberate input/hip
            // deceleration. Measure residual settling in the tail instead.
            if (_stopElapsed >= .75)
            {
                _stopSpeedSum += HorizontalSpeed(frame.Waist);
                _stopSamples++;
            }
            _stopOvershoot = Math.Max(_stopOvershoot,
                (new Vec3(frame.Waist.X, 0, frame.Waist.Z) - _stopOrigin).Length);
        }
        if (command == GaitCommand.TurnInPlace)
        {
            if (_previousCommand != GaitCommand.TurnInPlace) _turnElapsed = 0;
            _turnElapsed += dt;
            // Opposite toe oscillations should cancel around the turn heading.
            // Ignore the brief decay of translation inherited from the prior
            // segment; it is not a toe-turn failure.
            if (_turnElapsed >= .35 && frame.HorizontalSpeed < .025)
            {
                double footCenter = LerpAngle(frame.LeftFoot.Yaw, frame.RightFoot.Yaw, .5);
                double expectedCenter = WrapRadians(_head.Yaw + Math.Clamp(frame.YawRate * .08, -.25, .25));
                _toeErrorSum += AngleDistance(footCenter, expectedCenter) * 180 / Math.PI;
                _toeSamples++;
            }
        }
        if (!waist.IsFinite || !left.IsFinite || !right.IsFinite || !double.IsFinite(frame.Phase))
            _nonFinitePoseCount++;

        _time += dt;
        _previousCommand = command;
        return new GaitSample(_time, command, waist, left, right,
            frame.LeftPlanted, frame.RightPlanted,
            Wrap01(frame.Phase / (2 * Math.PI)),
            Wrap01(frame.Phase / (2 * Math.PI) + .5));
    }

    public GaitSample Advance(GaitCommand command, double dt = TickSeconds) => Step(command, dt);

    public IReadOnlyList<GaitSample> Run(
        IReadOnlyList<GaitScenarioSegment>? scenario = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<GaitScenarioSegment> rawSegments = scenario ?? GaitScenarios.Benchmark;
        if (rawSegments.Count > 128)
            throw new ArgumentOutOfRangeException(nameof(scenario), "A gait scenario may contain at most 128 segments.");
        var segments = new GaitScenarioSegment[rawSegments.Count];
        for (int i = 0; i < rawSegments.Count; i++)
        {
            GaitScenarioSegment? segment = rawSegments[i];
            if (segment is null)
                throw new ArgumentException("A gait scenario segment must not be null.", nameof(scenario));
            segments[i] = segment.Sanitize();
        }
        double totalDuration = segments.Sum(segment => segment.DurationSeconds);
        if (totalDuration > 120)
            throw new ArgumentOutOfRangeException(nameof(scenario), "A gait scenario may run for at most 120 seconds.");

        var samples = new List<GaitSample>();
        foreach (GaitScenarioSegment segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int ticks = (int)Math.Round(segment.DurationSeconds / TickSeconds, MidpointRounding.AwayFromZero);
            for (int i = 0; i < ticks; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                samples.Add(Step(segment.Command, TickSeconds, segment.SpeedMultiplier));
            }
        }
        return samples;
    }

    public IReadOnlyList<GaitSample> RunScenario(
        IReadOnlyList<GaitScenarioSegment>? scenario = null,
        CancellationToken cancellationToken = default) => Run(scenario, cancellationToken);

    public GaitMetrics BuildMetrics() => new(
        ElapsedSeconds: _time,
        PlantedFootWorldSlipMetersPerSecond: _plantWeightSum <= 0 ? 0 : _slipSum / _plantWeightSum,
        PlantedHeightErrorMeters: _plantWeightSum <= 0 ? 0 : _heightErrorSum / _plantWeightSum,
        SwingClearanceMeters: _peakClearance,
        WaistPeakAcceleration: _peakAcceleration,
        WaistPeakJerk: _peakJerk,
        LeftRightPhaseAsymmetry: CalculatePhaseAsymmetry(),
        StopSettlingSpeed: _stopSamples == 0 ? 0 : _stopSpeedSum / _stopSamples,
        StopOvershootMeters: _stopOvershoot,
        ToeTurnAlignmentDegrees: _toeSamples == 0
            ? (_turnDegrees >= 10 ? 180 : 0)
            : _toeErrorSum / _toeSamples,
        NonFinitePoseCount: _nonFinitePoseCount,
        SwingSteps: _swingSteps,
        CommandedTravelMeters: _commandedTravel,
        ActualTravelMeters: _actualTravel,
        TurnDegrees: _turnDegrees,
        MovementSeconds: _movementSeconds);

    private double CalculatePhaseAsymmetry()
    {
        double leftAverage = _leftCompletedSteps == 0 ? 0 : _leftStepSeconds / _leftCompletedSteps;
        double rightAverage = _rightCompletedSteps == 0 ? 0 : _rightStepSeconds / _rightCompletedSteps;
        double durationTotal = leftAverage + rightAverage;
        double durationDifference = durationTotal <= 0 ? 0 : Math.Abs(leftAverage - rightAverage) / durationTotal;
        int completedTotal = _leftCompletedSteps + _rightCompletedSteps;
        double stepDifference = completedTotal == 0
            ? 0
            : Math.Abs(_leftCompletedSteps - _rightCompletedSteps) / (double)completedTotal;
        return Math.Clamp((durationDifference + stepDifference) * .5, 0, 1);
    }

    private static BodyGaitParameters ToParameters(GaitProfile p) => new(
        p.BodyHeightMeters, p.HipFollowTau, p.HipLeanDegrees, p.FootSpacingMeters,
        p.StrideLengthMeters, p.StepHeightMeters, p.GaitSmoothingTau,
        p.TurnToeDegrees, p.FootPlantStrength);

    private static Vec3 DesiredWorldVelocity(GaitCommand command, double yaw)
    {
        Vec3 local = command switch
        {
            GaitCommand.Forward => new(0, 0, -WalkSpeed),
            GaitCommand.Strafe => new(StrafeSpeed, 0, 0),
            // The desktop driver does not normalize two simultaneous movement
            // keys, so this matches its actual diagonal HMD trace.
            GaitCommand.Diagonal => new(WalkSpeed, 0, -WalkSpeed),
            _ => Vec3.Zero,
        };
        double sin = Math.Sin(yaw), cos = Math.Cos(yaw);
        return new(local.X * cos + local.Z * sin, 0, -local.X * sin + local.Z * cos);
    }

    private static GaitPose ToPose(BodyGaitTrackerPose pose) => new(
        new Vec3(pose.X, pose.Y, pose.Z),
        new Vec3(pose.Vx, pose.Vy, pose.Vz),
        pose.Yaw * 180 / Math.PI);

    private static double HorizontalSpeed(BodyGaitTrackerPose pose) => Math.Sqrt(pose.Vx * pose.Vx + pose.Vz * pose.Vz);
    private static double PlantContactWeight(double phase)
    {
        double depth = Math.Max(0, -Math.Sin(phase));
        return depth * depth;
    }
    private static Vec3 Finite(Vec3 value) => new(Finite(value.X), Finite(value.Y), Finite(value.Z));
    private static double Finite(double value) => double.IsFinite(value) ? value : 0;
    private static double Wrap01(double value) => value - Math.Floor(value);
    private static double WrapRadians(double value)
    {
        value %= 2 * Math.PI;
        return value > Math.PI ? value - 2 * Math.PI : value < -Math.PI ? value + 2 * Math.PI : value;
    }
    private static double AngleDistance(double a, double b)
    {
        double value = WrapRadians(a - b);
        return Math.Abs(value);
    }
    private static double LerpAngle(double a, double b, double t) =>
        WrapRadians(a + WrapRadians(b - a) * Math.Clamp(t, 0, 1));
}
