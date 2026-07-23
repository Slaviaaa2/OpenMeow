using System.Globalization;
using System.Text;

namespace OpenMeow;

/// <summary>手ポーズの補間方式。Legacy は従来の一次遅れをそのまま使用する。</summary>
public enum MotionSmoothingMode
{
    Legacy,
    SecondOrder,
}

/// <summary>
/// デスクトップ入力の操作感プロファイル。
/// JSON/reflection を使わず、LocalAppData の不変行形式ファイルへ保存する。
/// </summary>
public sealed class DesktopMotionSettings
{
    private const string DefaultPreset = "Legacy";
    private const string SaveMutexName = @"Local\OpenMeow.MotionProfile.Config";
    private const double LegacyTurnDegrees = 70.0;
    private const double LegacyMouseDegrees = 0.15;
    private const double LegacyArmDegrees = 0.25;
    private const double LegacyWheelYawDegrees = 10.0;
    private const double DefaultBodyHeightMeters = 1.65;
    private const double DefaultHipFollowTau = 0.08;
    private const double DefaultHipLeanDegrees = 11.0;
    private const double DefaultFootSpacingMeters = 0.20;
    private const double DefaultStrideLengthMeters = 0.45;
    private const double DefaultStepHeightMeters = 0.06;
    private const double DefaultGaitSmoothingTau = 0.24;
    private const double DefaultTurnToeDegrees = 7.0;
    private const double DefaultFootPlantStrength = 0.92;

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenMeow", "motion-profile.cfg");

    public static IReadOnlyList<string> PresetNames { get; } = new[]
    {
        "Legacy", "Comfort", "Precise", "Natural", "Responsive",
    };

    public string Preset { get; set; } = DefaultPreset;
    public double MovementSpeed { get; set; } = 1.0;
    public double FastMultiplier { get; set; } = 2.5;
    public double SlowMultiplier { get; set; } = 0.3;
    public double TurnDegreesPerSecond { get; set; } = LegacyTurnDegrees;
    public double MovementSmoothTau { get; set; } = 0.15;
    public double MouseSensitivityDegreesPerPixel { get; set; } = LegacyMouseDegrees;
    public double ArmSensitivityDegreesPerPixel { get; set; } = LegacyArmDegrees;
    public double HandPositionSensitivity { get; set; } = 0.0012;
    public double WheelDepthStep { get; set; } = 0.05;
    public double WheelYawDegreesPerNotch { get; set; } = LegacyWheelYawDegrees;
    public double StickSensitivity { get; set; } = 0.011;
    public double StickReleaseTau { get; set; } = 0.06;
    public double HandPoseTau { get; set; } = 0.09;
    public MotionSmoothingMode HandSmoothingMode { get; set; } = MotionSmoothingMode.Legacy;
    public double HandSpringHz { get; set; } = 7.5;
    public double HandDamping { get; set; } = 1.0;
    public double HandMaxSpeed { get; set; } = 1.8;
    public double HandMaxAcceleration { get; set; } = 12.0;
    public double HandPredictionSeconds { get; set; } = 0.035;

    /// <summary>SteamVR GenericTracker トポロジーを初期化時に追加するか。既定は安全のため無効。</summary>
    public bool EnableBodyTrackers { get; set; }
    public double BodyHeightMeters { get; set; } = DefaultBodyHeightMeters;
    public double HipFollowTau { get; set; } = DefaultHipFollowTau;
    public double HipLeanDegrees { get; set; } = DefaultHipLeanDegrees;
    public double FootSpacingMeters { get; set; } = DefaultFootSpacingMeters;
    public double StrideLengthMeters { get; set; } = DefaultStrideLengthMeters;
    public double StepHeightMeters { get; set; } = DefaultStepHeightMeters;
    public double GaitSmoothingTau { get; set; } = DefaultGaitSmoothingTau;
    public double TurnToeDegrees { get; set; } = DefaultTurnToeDegrees;
    public double FootPlantStrength { get; set; } = DefaultFootPlantStrength;

    public DesktopMotionSettings Clone()
        => new()
        {
            Preset = Preset,
            MovementSpeed = MovementSpeed,
            FastMultiplier = FastMultiplier,
            SlowMultiplier = SlowMultiplier,
            TurnDegreesPerSecond = TurnDegreesPerSecond,
            MovementSmoothTau = MovementSmoothTau,
            MouseSensitivityDegreesPerPixel = MouseSensitivityDegreesPerPixel,
            ArmSensitivityDegreesPerPixel = ArmSensitivityDegreesPerPixel,
            HandPositionSensitivity = HandPositionSensitivity,
            WheelDepthStep = WheelDepthStep,
            WheelYawDegreesPerNotch = WheelYawDegreesPerNotch,
            StickSensitivity = StickSensitivity,
            StickReleaseTau = StickReleaseTau,
            HandPoseTau = HandPoseTau,
            HandSmoothingMode = HandSmoothingMode,
            HandSpringHz = HandSpringHz,
            HandDamping = HandDamping,
            HandMaxSpeed = HandMaxSpeed,
            HandMaxAcceleration = HandMaxAcceleration,
            HandPredictionSeconds = HandPredictionSeconds,
            EnableBodyTrackers = EnableBodyTrackers,
            BodyHeightMeters = BodyHeightMeters,
            HipFollowTau = HipFollowTau,
            HipLeanDegrees = HipLeanDegrees,
            FootSpacingMeters = FootSpacingMeters,
            StrideLengthMeters = StrideLengthMeters,
            StepHeightMeters = StepHeightMeters,
            GaitSmoothingTau = GaitSmoothingTau,
            TurnToeDegrees = TurnToeDegrees,
            FootPlantStrength = FootPlantStrength,
        };

    public static DesktopMotionSettings PresetOrLegacy(string? name)
    {
        return name?.Trim().ToLowerInvariant() switch
        {
            "comfort" => CreateComfort(),
            "precise" => CreatePrecise(),
            "natural" => CreateNatural(),
            "responsive" => CreateResponsive(),
            _ => Legacy(),
        };
    }

    public static DesktopMotionSettings Legacy() => new();

    public void ApplyPreset(string? name)
    {
        DesktopMotionSettings preset = PresetOrLegacy(name);
        Preset = preset.Preset;
        MovementSpeed = preset.MovementSpeed;
        FastMultiplier = preset.FastMultiplier;
        SlowMultiplier = preset.SlowMultiplier;
        TurnDegreesPerSecond = preset.TurnDegreesPerSecond;
        MovementSmoothTau = preset.MovementSmoothTau;
        MouseSensitivityDegreesPerPixel = preset.MouseSensitivityDegreesPerPixel;
        ArmSensitivityDegreesPerPixel = preset.ArmSensitivityDegreesPerPixel;
        HandPositionSensitivity = preset.HandPositionSensitivity;
        WheelDepthStep = preset.WheelDepthStep;
        WheelYawDegreesPerNotch = preset.WheelYawDegreesPerNotch;
        StickSensitivity = preset.StickSensitivity;
        StickReleaseTau = preset.StickReleaseTau;
        HandPoseTau = preset.HandPoseTau;
        HandSmoothingMode = preset.HandSmoothingMode;
        HandSpringHz = preset.HandSpringHz;
        HandDamping = preset.HandDamping;
        HandMaxSpeed = preset.HandMaxSpeed;
        HandMaxAcceleration = preset.HandMaxAcceleration;
        HandPredictionSeconds = preset.HandPredictionSeconds;
        // The topology flag is intentionally not part of a preset. Changing a
        // numeric preset must never silently require a SteamVR restart.
        BodyHeightMeters = preset.BodyHeightMeters;
        HipFollowTau = preset.HipFollowTau;
        HipLeanDegrees = preset.HipLeanDegrees;
        FootSpacingMeters = preset.FootSpacingMeters;
        StrideLengthMeters = preset.StrideLengthMeters;
        StepHeightMeters = preset.StepHeightMeters;
        GaitSmoothingTau = preset.GaitSmoothingTau;
        TurnToeDegrees = preset.TurnToeDegrees;
        FootPlantStrength = preset.FootPlantStrength;
    }

    public void Sanitize()
    {
        Preset = IsPreset(Preset) ? CanonicalPreset(Preset) : DefaultPreset;
        MovementSpeed = ClampFinite(MovementSpeed, 0.05, 5.0, 1.0);
        FastMultiplier = ClampFinite(FastMultiplier, 0.1, 8.0, 2.5);
        SlowMultiplier = ClampFinite(SlowMultiplier, 0.05, 1.0, 0.3);
        TurnDegreesPerSecond = ClampFinite(TurnDegreesPerSecond, 1.0, 360.0, LegacyTurnDegrees);
        MovementSmoothTau = ClampFinite(MovementSmoothTau, 0.01, 2.0, 0.15);
        MouseSensitivityDegreesPerPixel = ClampFinite(MouseSensitivityDegreesPerPixel, 0.005, 2.0, LegacyMouseDegrees);
        ArmSensitivityDegreesPerPixel = ClampFinite(ArmSensitivityDegreesPerPixel, 0.005, 3.0, LegacyArmDegrees);
        HandPositionSensitivity = ClampFinite(HandPositionSensitivity, 0.00005, 0.02, 0.0012);
        WheelDepthStep = ClampFinite(WheelDepthStep, 0.001, 0.5, 0.05);
        WheelYawDegreesPerNotch = ClampFinite(WheelYawDegreesPerNotch, 0.1, 90.0, LegacyWheelYawDegrees);
        StickSensitivity = ClampFinite(StickSensitivity, 0.0005, 0.1, 0.011);
        StickReleaseTau = ClampFinite(StickReleaseTau, 0.01, 2.0, 0.06);
        HandPoseTau = ClampFinite(HandPoseTau, 0.01, 2.0, 0.09);
        if (!Enum.IsDefined(HandSmoothingMode)) HandSmoothingMode = MotionSmoothingMode.Legacy;
        HandSpringHz = ClampFinite(HandSpringHz, 1.0, 30.0, 7.5);
        HandDamping = ClampFinite(HandDamping, 0.5, 2.0, 1.0);
        HandMaxSpeed = ClampFinite(HandMaxSpeed, 0.05, 10.0, 1.8);
        HandMaxAcceleration = ClampFinite(HandMaxAcceleration, 0.1, 100.0, 12.0);
        HandPredictionSeconds = ClampFinite(HandPredictionSeconds, 0.0, 0.15, 0.035);
        BodyHeightMeters = ClampFinite(BodyHeightMeters, 1.2, 2.2, DefaultBodyHeightMeters);
        HipFollowTau = ClampFinite(HipFollowTau, 0.03, 1.0, DefaultHipFollowTau);
        HipLeanDegrees = ClampFinite(HipLeanDegrees, 0.0, 25.0, DefaultHipLeanDegrees);
        FootSpacingMeters = ClampFinite(FootSpacingMeters, 0.10, 0.45, DefaultFootSpacingMeters);
        StrideLengthMeters = ClampFinite(StrideLengthMeters, 0.10, 1.2, DefaultStrideLengthMeters);
        StepHeightMeters = ClampFinite(StepHeightMeters, 0.0, 0.30, DefaultStepHeightMeters);
        GaitSmoothingTau = ClampFinite(GaitSmoothingTau, 0.02, 1.0, DefaultGaitSmoothingTau);
        TurnToeDegrees = ClampFinite(TurnToeDegrees, 0.0, 35.0, DefaultTurnToeDegrees);
        FootPlantStrength = ClampFinite(FootPlantStrength, 0.0, 1.0, DefaultFootPlantStrength);
    }

    public static DesktopMotionSettings LoadOrDefault()
    {
        if (!File.Exists(ConfigPath)) return Legacy();
        try
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in File.ReadAllLines(ConfigPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                int separator = line.IndexOf('=');
                if (separator <= 0) continue;
                values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }

            DesktopMotionSettings result = PresetOrLegacy(
                values.TryGetValue("Preset", out string? preset) ? preset : DefaultPreset);
            foreach (var entry in values)
            {
                if (!ApplyValue(result, entry.Key, entry.Value))
                    return Legacy();
            }
            result.Sanitize();
            return result;
        }
        catch
        {
            return Legacy();
        }
    }

    public void Save()
    {
        Sanitize();
        string directory = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(directory);
        string temp = ConfigPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        using var mutex = new Mutex(false, SaveMutexName);
        bool lockTaken = false;
        try
        {
            try
            {
                lockTaken = mutex.WaitOne(TimeSpan.FromSeconds(3));
            }
            catch (AbandonedMutexException)
            {
                lockTaken = true;
            }
            if (!lockTaken)
                throw new IOException("Timed out waiting to save the OpenMeow motion profile.");

            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.Read,
                       4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.WriteLine("# OpenMeow desktop motion profile (invariant)");
                writer.WriteLine($"Preset={Preset}");
                writer.WriteLine($"MovementSpeed={F(MovementSpeed)}");
                writer.WriteLine($"FastMultiplier={F(FastMultiplier)}");
                writer.WriteLine($"SlowMultiplier={F(SlowMultiplier)}");
                writer.WriteLine($"TurnDegreesPerSecond={F(TurnDegreesPerSecond)}");
                writer.WriteLine($"MovementSmoothTau={F(MovementSmoothTau)}");
                writer.WriteLine($"MouseSensitivityDegreesPerPixel={F(MouseSensitivityDegreesPerPixel)}");
                writer.WriteLine($"ArmSensitivityDegreesPerPixel={F(ArmSensitivityDegreesPerPixel)}");
                writer.WriteLine($"HandPositionSensitivity={F(HandPositionSensitivity)}");
                writer.WriteLine($"WheelDepthStep={F(WheelDepthStep)}");
                writer.WriteLine($"WheelYawDegreesPerNotch={F(WheelYawDegreesPerNotch)}");
                writer.WriteLine($"StickSensitivity={F(StickSensitivity)}");
                writer.WriteLine($"StickReleaseTau={F(StickReleaseTau)}");
                writer.WriteLine($"HandPoseTau={F(HandPoseTau)}");
                writer.WriteLine($"HandSmoothingMode={HandSmoothingMode}");
                writer.WriteLine($"HandSpringHz={F(HandSpringHz)}");
                writer.WriteLine($"HandDamping={F(HandDamping)}");
                writer.WriteLine($"HandMaxSpeed={F(HandMaxSpeed)}");
                writer.WriteLine($"HandMaxAcceleration={F(HandMaxAcceleration)}");
                writer.WriteLine($"HandPredictionSeconds={F(HandPredictionSeconds)}");
                writer.WriteLine($"EnableBodyTrackers={EnableBodyTrackers}");
                writer.WriteLine($"BodyHeightMeters={F(BodyHeightMeters)}");
                writer.WriteLine($"HipFollowTau={F(HipFollowTau)}");
                writer.WriteLine($"HipLeanDegrees={F(HipLeanDegrees)}");
                writer.WriteLine($"FootSpacingMeters={F(FootSpacingMeters)}");
                writer.WriteLine($"StrideLengthMeters={F(StrideLengthMeters)}");
                writer.WriteLine($"StepHeightMeters={F(StepHeightMeters)}");
                writer.WriteLine($"GaitSmoothingTau={F(GaitSmoothingTau)}");
                writer.WriteLine($"TurnToeDegrees={F(TurnToeDegrees)}");
                writer.WriteLine($"FootPlantStrength={F(FootPlantStrength)}");
            }
            File.Move(temp, ConfigPath, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            if (lockTaken) mutex.ReleaseMutex();
        }
    }

    private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static bool ApplyValue(DesktopMotionSettings target, string key, string value)
    {
        if (key.Equals("Preset", StringComparison.OrdinalIgnoreCase))
            return IsPreset(value) && (target.Preset = CanonicalPreset(value)) != null;
        if (key.Equals("EnableBodyTrackers", StringComparison.OrdinalIgnoreCase))
        {
            if (!bool.TryParse(value, out bool enabled)) return false;
            target.EnableBodyTrackers = enabled;
            return true;
        }
        if (key.Equals("HandSmoothingMode", StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse(value, true, out MotionSmoothingMode mode)) return false;
            target.HandSmoothingMode = mode;
            return true;
        }
        string normalizedKey = key.ToLowerInvariant();
        if (normalizedKey is not ("movementspeed" or "fastmultiplier" or "slowmultiplier"
            or "turndegreespersecond" or "movementsmoothtau" or "mousesensitivitydegreesperpixel"
            or "armsensitivitydegreesperpixel" or "handpositionsensitivity" or "wheeldepthstep"
            or "wheelyawdegreespernotch" or "sticksensitivity" or "stickreleasethau" or "handposetau"
            or "handspringhz" or "handdamping" or "handmaxspeed" or "handmaxacceleration"
            or "handpredictionseconds" or "bodyheightmeters" or "hipfollowtau" or "hipleandegrees"
            or "footspacingmeters" or "stridelengthmeters" or "stepheightmeters" or "gaitsmoothingtau"
            or "turntoedegrees" or "footplantstrength")) return true;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
            return false;
        switch (normalizedKey)
        {
            case "movementspeed": target.MovementSpeed = number; return InRange(number, 0.05, 5.0);
            case "fastmultiplier": target.FastMultiplier = number; return InRange(number, 0.1, 8.0);
            case "slowmultiplier": target.SlowMultiplier = number; return InRange(number, 0.05, 1.0);
            case "turndegreespersecond": target.TurnDegreesPerSecond = number; return InRange(number, 1, 360);
            case "movementsmoothtau": target.MovementSmoothTau = number; return InRange(number, 0.01, 2);
            case "mousesensitivitydegreesperpixel": target.MouseSensitivityDegreesPerPixel = number; return InRange(number, 0.005, 2);
            case "armsensitivitydegreesperpixel": target.ArmSensitivityDegreesPerPixel = number; return InRange(number, 0.005, 3);
            case "handpositionsensitivity": target.HandPositionSensitivity = number; return InRange(number, 0.00005, 0.02);
            case "wheeldepthstep": target.WheelDepthStep = number; return InRange(number, 0.001, 0.5);
            case "wheelyawdegreespernotch": target.WheelYawDegreesPerNotch = number; return InRange(number, 0.1, 90);
            case "sticksensitivity": target.StickSensitivity = number; return InRange(number, 0.0005, 0.1);
            case "stickreleasethau": target.StickReleaseTau = number; return InRange(number, 0.01, 2);
            case "handposetau": target.HandPoseTau = number; return InRange(number, 0.01, 2);
            case "handspringhz": target.HandSpringHz = number; return InRange(number, 1, 30);
            case "handdamping": target.HandDamping = number; return InRange(number, 0.5, 2);
            case "handmaxspeed": target.HandMaxSpeed = number; return InRange(number, 0.05, 10);
            case "handmaxacceleration": target.HandMaxAcceleration = number; return InRange(number, 0.1, 100);
            case "handpredictionseconds": target.HandPredictionSeconds = number; return InRange(number, 0, 0.15);
            case "bodyheightmeters": target.BodyHeightMeters = number; return InRange(number, 1.2, 2.2);
            case "hipfollowtau": target.HipFollowTau = number; return InRange(number, 0.03, 1.0);
            case "hipleandegrees": target.HipLeanDegrees = number; return InRange(number, 0, 25);
            case "footspacingmeters": target.FootSpacingMeters = number; return InRange(number, 0.10, 0.45);
            case "stridelengthmeters": target.StrideLengthMeters = number; return InRange(number, 0.10, 1.2);
            case "stepheightmeters": target.StepHeightMeters = number; return InRange(number, 0, 0.30);
            case "gaitsmoothingtau": target.GaitSmoothingTau = number; return InRange(number, 0.02, 1.0);
            case "turntoedegrees": target.TurnToeDegrees = number; return InRange(number, 0, 35);
            case "footplantstrength": target.FootPlantStrength = number; return InRange(number, 0, 1);
            default: return true;
        }
    }

    private static bool InRange(double value, double min, double max) => value >= min && value <= max;
    private static double ClampFinite(double value, double min, double max, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    private static bool IsPreset(string? value)
        => value is not null && PresetNames.Any(x => x.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
    private static string CanonicalPreset(string value)
        => PresetNames.First(x => x.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));

    private static DesktopMotionSettings CreateComfort() => FromPreset("Comfort", 0.8, 2.0, 0.25, 55, 0.20, 0.12, 0.18, 0.0009, 0.04, 8, 0.009, 0.09, MotionSmoothingMode.SecondOrder, 5.5, 1.35, 1.3, 8, 0.025,
        1.65, 0.18, 6, 0.21, 0.45, 0.06, 0.15, 8, 0.86);
    private static DesktopMotionSettings CreatePrecise() => FromPreset("Precise", 1.0, 2.5, 0.3, 70, 0.10, 0.09, 0.15, 0.0006, 0.03, 6, 0.007, 0.05, MotionSmoothingMode.SecondOrder, 6, 1.4, 1.0, 7, 0.015,
        1.68, 0.12, 5, 0.20, 0.38, 0.05, 0.10, 6, 0.90);
    // Cross-task maximin winner after Lab v3 added explicit settling, axial
    // stroke speed and smooth target trajectories. It gives up task-specific
    // peak scores to avoid a weak cheek/hold result. Whole-body values are the
    // rounded maximin winner from shared-kernel evaluator v8 across default,
    // stop, turn, transitions, endurance, Fast and Slow scenarios.
    private static DesktopMotionSettings CreateNatural() => FromPreset("Natural", 0.9, 2.25, 0.25, 60, 0.18, 0.14, 0.20, 0.0010, 0.045, 9, 0.010, 0.08, MotionSmoothingMode.SecondOrder, 6.0, 1.1, 1.5, 14, 0.020,
        1.65, 0.08, 11, 0.20, 0.45, 0.06, 0.24, 7, 0.92);
    private static DesktopMotionSettings CreateResponsive() => FromPreset("Responsive", 1.1, 3.0, 0.35, 80, 0.10, 0.18, 0.30, 0.0014, 0.055, 11, 0.013, 0.045, MotionSmoothingMode.SecondOrder, 9, 1.05, 2.5, 22, 0.025,
        1.65, 0.10, 10, 0.21, 0.62, 0.09, 0.09, 12, 0.74);

    private static DesktopMotionSettings FromPreset(string name, double move, double fast, double slow, double turn,
        double smooth, double mouse, double arm, double hand, double depth, double yaw, double stick, double release,
        MotionSmoothingMode mode, double hz, double damping, double maxSpeed, double maxAcceleration, double prediction,
        double bodyHeight, double hipTau, double hipLean, double footSpacing, double stride, double stepHeight,
        double gaitTau, double toeDegrees, double footPlant)
        => new()
        {
            Preset = name,
            MovementSpeed = move,
            FastMultiplier = fast,
            SlowMultiplier = slow,
            TurnDegreesPerSecond = turn,
            MovementSmoothTau = smooth,
            MouseSensitivityDegreesPerPixel = mouse,
            ArmSensitivityDegreesPerPixel = arm,
            HandPositionSensitivity = hand,
            WheelDepthStep = depth,
            WheelYawDegreesPerNotch = yaw,
            StickSensitivity = stick,
            StickReleaseTau = release,
            HandPoseTau = 0.09,
            HandSmoothingMode = mode,
            HandSpringHz = hz,
            HandDamping = damping,
            HandMaxSpeed = maxSpeed,
            HandMaxAcceleration = maxAcceleration,
            HandPredictionSeconds = prediction,
            BodyHeightMeters = bodyHeight,
            HipFollowTau = hipTau,
            HipLeanDegrees = hipLean,
            FootSpacingMeters = footSpacing,
            StrideLengthMeters = stride,
            StepHeightMeters = stepHeight,
            GaitSmoothingTau = gaitTau,
            TurnToeDegrees = toeDegrees,
            FootPlantStrength = footPlant,
        };
}
