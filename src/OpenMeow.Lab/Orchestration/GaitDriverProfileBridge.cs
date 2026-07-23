using OpenMeow;
using OpenMeow.Lab.Domain;

namespace OpenMeow.Lab.Orchestration;

/// <summary>Result of projecting a Lab gait profile onto desktop settings.</summary>
public sealed record GaitDriverProfileBridgeResult(
    string BasePreset,
    GaitProfile Profile,
    DesktopMotionSettings Settings,
    string ConfigPath,
    bool Applied,
    bool EnableBodyTrackers,
    bool RestartRequired);

/// <summary>
/// Maps all nine gait fields to the desktop settings. Preview is read-only and
/// retains the current tracker-topology flag; apply is the only writing path.
/// </summary>
public static class GaitDriverProfileBridge
{
    public static GaitDriverProfileBridgeResult Preview(GaitProfile profile, string? basePreset = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string preset = ResolvePreset(basePreset);
        GaitProfile source = profile.Sanitize();
        DesktopMotionSettings topology = DesktopMotionSettings.LoadOrDefault();
        DesktopMotionSettings settings = DesktopMotionSettings.PresetOrLegacy(preset).Clone();
        settings.Preset = preset;
        settings.EnableBodyTrackers = topology.EnableBodyTrackers;
        Map(source, settings);
        settings.Sanitize();
        return new GaitDriverProfileBridgeResult(
            preset,
            source,
            settings,
            DesktopMotionSettings.ConfigPath,
            Applied: false,
            EnableBodyTrackers: settings.EnableBodyTrackers,
            RestartRequired: false);
    }

    public static GaitDriverProfileBridgeResult Apply(
        GaitProfile profile,
        string? basePreset,
        bool enableBodyTrackers)
    {
        GaitDriverProfileBridgeResult preview = Preview(profile, basePreset);
        DesktopMotionSettings settings = preview.Settings.Clone();
        settings.EnableBodyTrackers = enableBodyTrackers;
        bool restartRequired = preview.EnableBodyTrackers != enableBodyTrackers;
        settings.Save();
        return preview with
        {
            Settings = settings,
            Applied = true,
            EnableBodyTrackers = enableBodyTrackers,
            RestartRequired = restartRequired,
        };
    }

    private static void Map(GaitProfile source, DesktopMotionSettings target)
    {
        target.BodyHeightMeters = source.BodyHeightMeters;
        target.HipFollowTau = source.HipFollowTau;
        target.HipLeanDegrees = source.HipLeanDegrees;
        target.FootSpacingMeters = source.FootSpacingMeters;
        target.StrideLengthMeters = source.StrideLengthMeters;
        target.StepHeightMeters = source.StepHeightMeters;
        target.GaitSmoothingTau = source.GaitSmoothingTau;
        target.TurnToeDegrees = source.TurnToeDegrees;
        target.FootPlantStrength = source.FootPlantStrength;
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
