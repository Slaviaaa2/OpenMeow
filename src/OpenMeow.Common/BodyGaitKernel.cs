namespace OpenMeow;

/// <summary>HMD のワールド位置とヨー角。角度はラジアン。</summary>
public readonly struct BodyGaitHeadPose
{
    /// <summary>ワールド X 座標。</summary>
    public readonly double X;
    /// <summary>ワールド Y 座標。</summary>
    public readonly double Y;
    /// <summary>ワールド Z 座標。</summary>
    public readonly double Z;
    /// <summary>ヨー角 (ラジアン)。</summary>
    public readonly double Yaw;

    /// <summary>指定したワールド位置とヨー角から頭ポーズを作成する。</summary>
    public BodyGaitHeadPose(double x, double y, double z, double yaw)
    {
        X = x;
        Y = y;
        Z = z;
        Yaw = yaw;
    }
}

/// <summary>腰または足トラッカーの一フレーム姿勢。</summary>
public readonly struct BodyGaitTrackerPose
{
    /// <summary>ワールド X 座標。</summary>
    public readonly double X;
    /// <summary>ワールド Y 座標。</summary>
    public readonly double Y;
    /// <summary>ワールド Z 座標。</summary>
    public readonly double Z;
    /// <summary>ワールド X 速度 (m/s)。</summary>
    public readonly double Vx;
    /// <summary>ワールド Y 速度 (m/s)。</summary>
    public readonly double Vy;
    /// <summary>ワールド Z 速度 (m/s)。</summary>
    public readonly double Vz;
    /// <summary>ヨー角 (ラジアン)。</summary>
    public readonly double Yaw;
    /// <summary>ピッチ角 (ラジアン)。</summary>
    public readonly double Pitch;
    /// <summary>ロール角 (ラジアン)。</summary>
    public readonly double Roll;
    /// <summary>ヨー角速度 (rad/s)。</summary>
    public readonly double AngularVelocityYaw;
    /// <summary>トラッカーが初期化済みかどうか。</summary>
    public readonly bool Initialized;

    /// <summary>トラッカーの位置・速度・向きから姿勢を作成する。</summary>
    public BodyGaitTrackerPose(
        double x, double y, double z,
        double vx, double vy, double vz,
        double yaw, double pitch, double roll, bool initialized, double angularVelocityYaw = 0)
    {
        X = x;
        Y = y;
        Z = z;
        Vx = vx;
        Vy = vy;
        Vz = vz;
        Yaw = yaw;
        Pitch = pitch;
        Roll = roll;
        AngularVelocityYaw = angularVelocityYaw;
        Initialized = initialized;
    }

    /// <summary>角速度を持たない従来形式の姿勢を作成する。</summary>
    public BodyGaitTrackerPose(
        double x, double y, double z,
        double vx, double vy, double vz,
        double yaw, double pitch, double roll, bool initialized)
        : this(x, y, z, vx, vy, vz, yaw, pitch, roll, initialized, 0)
    {
    }

    /// <summary>未初期化のゼロ姿勢。</summary>
    public static BodyGaitTrackerPose Default => new(0, 0, 0, 0, 0, 0, 0, 0, 0, false);
}

/// <summary>歩行の調整値。数値は <see cref="DesktopMotionSettings"/> の同名項目と対応する。</summary>
public readonly struct BodyGaitParameters
{
    /// <summary>頭から床までの身長 (m)。</summary>
    public readonly double BodyHeightMeters;
    /// <summary>腰が頭へ追従する時定数 (s)。</summary>
    public readonly double HipFollowTau;
    /// <summary>腰の前後・左右の傾き上限 (度)。</summary>
    public readonly double HipLeanDegrees;
    /// <summary>両足の左右間隔 (m)。</summary>
    public readonly double FootSpacingMeters;
    /// <summary>一歩の前後長 (m)。</summary>
    public readonly double StrideLengthMeters;
    /// <summary>足を上げる高さ (m)。</summary>
    public readonly double StepHeightMeters;
    /// <summary>速度と足の追従に使う時定数 (s)。</summary>
    public readonly double GaitSmoothingTau;
    /// <summary>旋回時のつま先角度 (度)。</summary>
    public readonly double TurnToeDegrees;
    /// <summary>接地足をワールドへ固定する強さ (0..1)。</summary>
    public readonly double FootPlantStrength;

    /// <summary>9 個の歩行調整値からパラメータを作成する。</summary>
    public BodyGaitParameters(
        double bodyHeightMeters, double hipFollowTau, double hipLeanDegrees,
        double footSpacingMeters, double strideLengthMeters, double stepHeightMeters,
        double gaitSmoothingTau, double turnToeDegrees, double footPlantStrength)
    {
        BodyHeightMeters = bodyHeightMeters;
        HipFollowTau = hipFollowTau;
        HipLeanDegrees = hipLeanDegrees;
        FootSpacingMeters = footSpacingMeters;
        StrideLengthMeters = strideLengthMeters;
        StepHeightMeters = stepHeightMeters;
        GaitSmoothingTau = gaitSmoothingTau;
        TurnToeDegrees = turnToeDegrees;
        FootPlantStrength = footPlantStrength;
    }

    /// <summary>OpenMeow の自然歩行既定値。</summary>
    public static BodyGaitParameters Natural => new(1.65, 0.08, 11, 0.20, 0.45, 0.06, 0.24, 7, 0.92);
}

/// <summary>歩行カーネルの出力。研究用シミュレータと実ドライバが同じ式を使うための値。</summary>
public readonly struct BodyGaitFrame
{
    /// <summary>腰の姿勢。</summary>
    public readonly BodyGaitTrackerPose Waist;
    /// <summary>左足の姿勢。</summary>
    public readonly BodyGaitTrackerPose LeftFoot;
    /// <summary>右足の姿勢。</summary>
    public readonly BodyGaitTrackerPose RightFoot;
    /// <summary>平滑化済みの頭ヨー角速度 (rad/s)。</summary>
    public readonly double YawRate;
    /// <summary>歩行位相 (ラジアン)。</summary>
    public readonly double Phase;
    /// <summary>平滑化済み水平速度 (m/s)。</summary>
    public readonly double HorizontalSpeed;
    /// <summary>左足が接地側の位相かどうか。</summary>
    public readonly bool LeftPlanted;
    /// <summary>右足が接地側の位相かどうか。</summary>
    public readonly bool RightPlanted;

    /// <summary>腰・両足と歩行状態からフレームを作成する。</summary>
    public BodyGaitFrame(
        BodyGaitTrackerPose waist, BodyGaitTrackerPose leftFoot, BodyGaitTrackerPose rightFoot,
        double yawRate, double phase, double horizontalSpeed, bool leftPlanted, bool rightPlanted)
    {
        Waist = waist;
        LeftFoot = leftFoot;
        RightFoot = rightFoot;
        YawRate = yawRate;
        Phase = phase;
        HorizontalSpeed = horizontalSpeed;
        LeftPlanted = leftPlanted;
        RightPlanted = rightPlanted;
    }

    /// <summary>未初期化のゼロフレーム。</summary>
    public static BodyGaitFrame Default => new(
        BodyGaitTrackerPose.Default, BodyGaitTrackerPose.Default, BodyGaitTrackerPose.Default,
        0, 0, 0, true, true);
}

/// <summary>
/// HMD の軌跡から腰・両足を決定論的に生成する、割り当てなしの状態付きカーネル。
/// </summary>
public sealed class BodyGaitKernel
{
    private const double DefaultBodyHeight = 1.65;
    private const double DefaultHipFollowTau = 0.08;
    private const double DefaultHipLeanDegrees = 11;
    private const double DefaultFootSpacing = 0.20;
    private const double DefaultStrideLength = 0.45;
    private const double DefaultStepHeight = 0.06;
    private const double DefaultSmoothingTau = 0.24;
    private const double DefaultTurnToeDegrees = 7;
    private const double DefaultPlantStrength = 0.92;

    private BodyGaitParameters _parameters = BodyGaitParameters.Natural;
    private BodyGaitFrame _frame = BodyGaitFrame.Default;
    private double _prevHeadX, _prevHeadY, _prevHeadZ, _prevHeadYaw;
    private double _referenceHeadY = DefaultBodyHeight;
    private double _velocityX, _velocityZ, _yawRate;
    private double _phase, _hipYaw;
    private bool _initialized;

    /// <summary>現在の出力フレーム。</summary>
    public BodyGaitFrame CurrentFrame => _frame;

    /// <summary>現在の歩行パラメータを有限値へ正規化して設定する。</summary>
    public void Configure(BodyGaitParameters parameters)
    {
        _parameters = Sanitize(parameters);
        Reset(new BodyGaitHeadPose(0, DefaultBodyHeight, 0, 0));
    }

    /// <summary>内部状態を指定した頭位置で初期化する。</summary>
    public void Reset(BodyGaitHeadPose head)
    {
        head = SafeHead(head, _initialized ? new BodyGaitHeadPose(_prevHeadX, _prevHeadY, _prevHeadZ, _prevHeadYaw) : new BodyGaitHeadPose(0, DefaultBodyHeight, 0, 0));
        _prevHeadX = head.X;
        _prevHeadY = head.Y;
        _prevHeadZ = head.Z;
        _prevHeadYaw = head.Yaw;
        _referenceHeadY = head.Y;
        _velocityX = _velocityZ = _yawRate = 0;
        _phase = 0;
        _hipYaw = head.Yaw;
        _initialized = false;
        _frame = BodyGaitFrame.Default;
    }

    /// <summary>一フレーム進め、腰・足の有限な姿勢を返す。</summary>
    public BodyGaitFrame Step(double dt, BodyGaitHeadPose head, bool reset = false)
    {
        dt = !double.IsFinite(dt) || dt <= 0 ? 1.0 / 90.0 : Math.Clamp(dt, 0.0001, 0.1);
        head = SafeHead(head, new BodyGaitHeadPose(_prevHeadX, _prevHeadY, _prevHeadZ, _prevHeadYaw));
        if (reset) Reset(head);
        if (!_initialized)
        {
            _prevHeadX = head.X;
            _prevHeadY = head.Y;
            _prevHeadZ = head.Z;
            _prevHeadYaw = head.Yaw;
            _referenceHeadY = head.Y;
            _hipYaw = head.Yaw;
            _initialized = true;
        }

        double rawVx = ClampFinite((head.X - _prevHeadX) / dt, -20, 20, 0);
        double rawVz = ClampFinite((head.Z - _prevHeadZ) / dt, -20, 20, 0);
        double rawYawRate = ClampFinite(WrapAngle(head.Yaw - _prevHeadYaw) / dt, -20, 20, 0);
        _prevHeadX = head.X;
        _prevHeadY = head.Y;
        _prevHeadZ = head.Z;
        _prevHeadYaw = head.Yaw;

        double velocityAlpha = 1 - Math.Exp(-dt / Math.Max(0.02, _parameters.GaitSmoothingTau));
        _velocityX += (rawVx - _velocityX) * velocityAlpha;
        _velocityZ += (rawVz - _velocityZ) * velocityAlpha;
        _yawRate += (rawYawRate - _yawRate) * velocityAlpha;
        _velocityX = Finite(_velocityX); _velocityZ = Finite(_velocityZ); _yawRate = Finite(_yawRate);

        double sinHead = Math.Sin(head.Yaw), cosHead = Math.Cos(head.Yaw);
        double forwardSpeed = -(_velocityX * sinHead + _velocityZ * cosHead);
        double lateralSpeed = _velocityX * cosHead - _velocityZ * sinHead;
        double horizontalSpeed = Math.Sqrt(_velocityX * _velocityX + _velocityZ * _velocityZ);
        double stride = Math.Max(0.1, _parameters.StrideLengthMeters);
        double drive = horizontalSpeed + Math.Abs(_yawRate) * 0.25;
        if (drive > 0.025)
        {
            // One phase cycle contains a left and a right step. StrideLength is
            // the distance of one step, so a full two-foot cycle covers two
            // strides. Dividing by only one stride doubled human cadence.
            double cyclesPerSecond = Math.Clamp(drive / (stride * 2) * 0.85, 0.25, 2.5);
            _phase += 2 * Math.PI * cyclesPerSecond * dt;
            _phase = WrapCycle(_phase);
        }
        else
        {
            // Finish only the currently swinging half-step. Decaying an
            // accumulated multi-cycle phase toward zero made both feet replay
            // every prior step backwards whenever the user stopped.
            double landing = Math.Ceiling((_phase - 1e-9) / Math.PI) * Math.PI;
            landing = Math.Clamp(landing, 0, 2 * Math.PI);
            _phase += (landing - _phase) * (1 - Math.Exp(-dt / 0.12));
            if (Math.Abs(landing - _phase) < 1e-5)
                _phase = landing >= 2 * Math.PI ? 0 : landing;
        }

        // Calibrate the current HMD height as floor-relative zero. BodyHeight
        // controls avatar proportions; it must not push feet below the floor
        // merely because the desktop HMD starts at a different Y coordinate.
        double floorY = head.Y - _referenceHeadY;
        double hipHeight = _parameters.BodyHeightMeters * 0.55;
        double hipTargetX = head.X, hipTargetY = floorY + hipHeight, hipTargetZ = head.Z;
        BodyGaitTrackerPose waist = _frame.Waist;
        double hipAlpha = 1 - Math.Exp(-dt / Math.Max(0.03, _parameters.HipFollowTau));
        if (!waist.Initialized)
            waist = new BodyGaitTrackerPose(hipTargetX, hipTargetY, hipTargetZ, 0, 0, 0, head.Yaw, 0, 0, true);
        double oldHipX = waist.X, oldHipY = waist.Y, oldHipZ = waist.Z;
        double hipX = waist.X + (hipTargetX - waist.X) * hipAlpha;
        double hipY = waist.Y + (hipTargetY - waist.Y) * hipAlpha;
        double hipZ = waist.Z + (hipTargetZ - waist.Z) * hipAlpha;
        _hipYaw += WrapAngle(head.Yaw - _hipYaw) * hipAlpha;
        double leanScale = _parameters.HipLeanDegrees * Math.PI / 180.0;
        double waistYawVelocity = Finite(WrapAngle(_hipYaw - waist.Yaw) / dt);
        waist = new BodyGaitTrackerPose(hipX, hipY, hipZ,
            Finite((hipX - oldHipX) / dt), Finite((hipY - oldHipY) / dt), Finite((hipZ - oldHipZ) / dt),
            Finite(_hipYaw), Math.Clamp(-forwardSpeed * leanScale, -leanScale, leanScale),
            Math.Clamp(-lateralSpeed * leanScale, -leanScale, leanScale), true, waistYawVelocity);

        double moveX, moveZ;
        if (horizontalSpeed > 0.03)
        {
            moveX = _velocityX / horizontalSpeed;
            moveZ = _velocityZ / horizontalSpeed;
        }
        else
        {
            moveX = -sinHead;
            moveZ = -cosHead;
        }
        double moveYaw = Math.Atan2(-moveX, -moveZ);
        double tangentX = cosHead, tangentZ = sinHead;
        double spacing = Math.Max(0.10, _parameters.FootSpacingMeters) * 0.5;
        double footY = floorY + 0.025;
        BodyGaitTrackerPose left = UpdateFoot(_frame.LeftFoot, -1, _phase, moveX, moveZ, moveYaw, tangentX, tangentZ, spacing, footY, dt, head);
        BodyGaitTrackerPose right = UpdateFoot(_frame.RightFoot, +1, _phase + Math.PI, moveX, moveZ, moveYaw, tangentX, tangentZ, spacing, footY, dt, head);
        const double contactEpsilon = 1e-9;
        bool leftPlanted = Math.Sin(_phase) <= contactEpsilon;
        bool rightPlanted = Math.Sin(_phase + Math.PI) <= contactEpsilon;
        _frame = new BodyGaitFrame(waist, left, right, _yawRate, _phase, horizontalSpeed, leftPlanted, rightPlanted);
        return _frame;
    }

    private BodyGaitTrackerPose UpdateFoot(BodyGaitTrackerPose foot, int side, double phase,
        double moveX, double moveZ, double moveYaw, double tangentX, double tangentZ,
        double spacing, double floorY, double dt, BodyGaitHeadPose head)
    {
        double wave = Math.Sin(phase);
        double strideBlend = SmoothStep((wave + 1) * 0.5) - 0.5;
        double swing = SmoothStep(Math.Max(0, wave));
        double localX = side * spacing;
        double targetX = head.X + localX * Math.Cos(head.Yaw) + moveX * strideBlend * _parameters.StrideLengthMeters;
        double targetZ = head.Z - localX * Math.Sin(head.Yaw) + moveZ * strideBlend * _parameters.StrideLengthMeters;
        double turnStep = Math.Clamp(_yawRate, -4, 4) * 0.025 * Math.Sin(phase);
        targetX += tangentX * turnStep;
        targetZ += tangentZ * turnStep;
        double plant = Math.Clamp(_parameters.FootPlantStrength, 0, 1);
        // Give the upper part of the control a useful "nearly planted" range:
        // 0.90 becomes 0.99 world-lock at mid-stance while 0 remains free.
        // The phase envelope still releases the foot continuously at toe-off.
        double plantLock = 1 - (1 - plant) * (1 - plant);
        double contactDepth = Math.Clamp(-wave, 0, 1);
        // Reach full stance lock before mid-stance, leaving only the edges for
        // smooth heel-strike/toe-off. A linear envelope visibly dragged feet.
        double stanceEnvelope = SmoothStep(Math.Min(1, contactDepth * 2));
        double stanceBlend = stanceEnvelope * plantLock;
        if (foot.Initialized)
        {
            targetX = targetX * (1 - stanceBlend) + foot.X * stanceBlend;
            targetZ = targetZ * (1 - stanceBlend) + foot.Z * stanceBlend;
        }
        double targetY = floorY + _parameters.StepHeightMeters * swing;
        double alpha = 1 - Math.Exp(-dt / Math.Max(0.02, _parameters.GaitSmoothingTau));
        double toe = Math.Clamp(_parameters.TurnToeDegrees * Math.PI / 180.0, 0, 0.61);
        double finalYaw = Finite(moveYaw + toe * Math.Sin(phase) + Math.Clamp(_yawRate * 0.08, -0.25, 0.25));
        if (!foot.Initialized)
            foot = new BodyGaitTrackerPose(targetX, targetY, targetZ, 0, 0, 0, finalYaw, 0, 0, true);
        double oldX = foot.X, oldY = foot.Y, oldZ = foot.Z;
        double oldYaw = foot.Yaw;
        double x = foot.X + (targetX - foot.X) * alpha;
        double y = foot.Y + (targetY - foot.Y) * alpha;
        double z = foot.Z + (targetZ - foot.Z) * alpha;
        return new BodyGaitTrackerPose(x, y, z,
            Finite((x - oldX) / dt), Finite((y - oldY) / dt), Finite((z - oldZ) / dt),
            finalYaw, Math.Clamp(-0.10 * wave, -0.20, 0.20),
            Math.Clamp(side * _yawRate * 0.01, -0.12, 0.12), true,
            Finite(WrapAngle(finalYaw - oldYaw) / dt));
    }

    private static BodyGaitParameters Sanitize(BodyGaitParameters p)
        => new(ClampFinite(p.BodyHeightMeters, 1.2, 2.2, DefaultBodyHeight),
            ClampFinite(p.HipFollowTau, 0.03, 1.0, DefaultHipFollowTau),
            ClampFinite(p.HipLeanDegrees, 0, 25, DefaultHipLeanDegrees),
            ClampFinite(p.FootSpacingMeters, 0.10, 0.45, DefaultFootSpacing),
            ClampFinite(p.StrideLengthMeters, 0.10, 1.2, DefaultStrideLength),
            ClampFinite(p.StepHeightMeters, 0, 0.30, DefaultStepHeight),
            ClampFinite(p.GaitSmoothingTau, 0.02, 1.0, DefaultSmoothingTau),
            ClampFinite(p.TurnToeDegrees, 0, 35, DefaultTurnToeDegrees),
            ClampFinite(p.FootPlantStrength, 0, 1, DefaultPlantStrength));

    private static BodyGaitHeadPose SafeHead(BodyGaitHeadPose value, BodyGaitHeadPose fallback)
        => new(ClampFinite(value.X, -1_000_000, 1_000_000, fallback.X),
            ClampFinite(value.Y, -1_000_000, 1_000_000, fallback.Y),
            ClampFinite(value.Z, -1_000_000, 1_000_000, fallback.Z),
            ClampFinite(value.Yaw, -1_000_000, 1_000_000, fallback.Yaw));

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;
    private static double ClampFinite(double value, double min, double max, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    private static double SmoothStep(double value)
    {
        double x = Math.Clamp(value, 0, 1);
        return x * x * (3 - 2 * x);
    }
    private static double WrapAngle(double rad)
    {
        while (rad > Math.PI) rad -= 2 * Math.PI;
        while (rad < -Math.PI) rad += 2 * Math.PI;
        return rad;
    }

    private static double WrapCycle(double rad)
    {
        double cycle = 2 * Math.PI;
        rad %= cycle;
        return rad < 0 ? rad + cycle : rad;
    }
}
