using OpenMeow;
using OpenMeow.Lab.Domain;

namespace OpenMeow.Lab.Orchestration;

/// <summary>
/// Describes the explicit hand-motion profile projection from the research
/// harness into the desktop driver's fixed settings file.
/// </summary>
public sealed record DriverProfileBridgeResult(
    string BasePreset,
    string ProfileName,
    DesktopMotionSettings Settings,
    string ConfigPath,
    bool Applied);

/// <summary>
/// Maps only parameters that have an equivalent in the desktop second-order
/// hand solver. Research-only contact and binding fields remain in the Lab
/// profile and are never silently written to the driver.
/// </summary>
public static class DriverProfileBridge
{
    public static DriverProfileBridgeResult Preview(MotionProfile profile, string? basePreset = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string preset = ResolvePreset(basePreset);
        MotionProfile source = profile.Sanitize();
        DesktopMotionSettings current = DesktopMotionSettings.LoadOrDefault();
        DesktopMotionSettings settings = DesktopMotionSettings.PresetOrLegacy(preset).Clone();

        // Keep all locomotion, sensitivity and release values from the selected
        // desktop preset. These five values are the only shared control surface
        // between the Lab's hand solver and the driver's second-order solver.
        settings.Preset = preset;
        settings.HandSmoothingMode = MotionSmoothingMode.SecondOrder;
        settings.HandSpringHz = Math.Clamp(source.PositionSpringHz, 1.0, 30.0);
        // The driver clamps damping to >= 1 at runtime; doing so here keeps a
        // preview faithful to the value that will actually be applied.
        settings.HandDamping = Math.Clamp(Math.Max(1.0, source.DampingRatio), 1.0, 2.0);
        settings.HandMaxSpeed = Math.Clamp(source.MaxSpeed, 0.05, 10.0);
        settings.HandMaxAcceleration = Math.Clamp(source.MaxAcceleration, 0.1, 100.0);
        settings.HandPredictionSeconds = Math.Clamp(source.PredictionSeconds, 0.0, 0.15);
        // Hand-profile research must not silently alter whole-body topology or
        // gait tuning already selected in the desktop config.
        settings.EnableBodyTrackers = current.EnableBodyTrackers;
        settings.BodyHeightMeters = current.BodyHeightMeters;
        settings.HipFollowTau = current.HipFollowTau;
        settings.HipLeanDegrees = current.HipLeanDegrees;
        settings.FootSpacingMeters = current.FootSpacingMeters;
        settings.StrideLengthMeters = current.StrideLengthMeters;
        settings.StepHeightMeters = current.StepHeightMeters;
        settings.GaitSmoothingTau = current.GaitSmoothingTau;
        settings.TurnToeDegrees = current.TurnToeDegrees;
        settings.FootPlantStrength = current.FootPlantStrength;
        settings.Sanitize();

        return new DriverProfileBridgeResult(
            preset,
            source.Name,
            settings,
            DesktopMotionSettings.ConfigPath,
            false);
    }

    public static DriverProfileBridgeResult Apply(MotionProfile profile, string? basePreset = null)
    {
        DriverProfileBridgeResult preview = Preview(profile, basePreset);
        // Apply is intentionally the only operation that writes the driver's
        // fixed LocalAppData config. The Lab never calls it during research.
        preview.Settings.Save();
        return preview with { Applied = true };
    }

    private static string ResolvePreset(string? value)
    {
        string requested = string.IsNullOrWhiteSpace(value) ? "Natural" : value.Trim();
        return DesktopMotionSettings.PresetNames.FirstOrDefault(
                   preset => preset.Equals(requested, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException(
                   $"Unknown desktop base preset '{requested}'. Choose one of {string.Join(", ", DesktopMotionSettings.PresetNames)}.",
                   nameof(value));
    }
}
