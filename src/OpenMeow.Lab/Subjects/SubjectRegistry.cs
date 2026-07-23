using System.Text.Json;
using OpenMeow.Lab.Domain;

namespace OpenMeow.Lab.Subjects;

public sealed class SubjectRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, SubjectDefinition> _subjects =
        new(StringComparer.OrdinalIgnoreCase);

    public SubjectRegistry(string? modelDirectory = null)
    {
        Register(CreateDefault());
        if (!string.IsNullOrWhiteSpace(modelDirectory))
            LoadDirectory(modelDirectory);
    }

    public IReadOnlyList<SubjectDefinition> All =>
        _subjects.Values.OrderBy(subject => subject.DisplayName).ToArray();

    public SubjectDefinition Get(string id) =>
        _subjects.TryGetValue(id, out SubjectDefinition? subject)
            ? subject
            : throw new KeyNotFoundException($"Unknown subject '{id}'.");

    public void Register(SubjectDefinition subject)
    {
        Validate(subject);
        _subjects[subject.Id] = subject;
    }

    public int LoadDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        int count = 0;
        foreach (string path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                SubjectDefinition? subject = JsonSerializer.Deserialize<SubjectDefinition>(
                    File.ReadAllText(path), JsonOptions);
                if (subject is null) continue;
                Register(subject);
                count++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[models] ignored {Path.GetFileName(path)}: {ex.Message}");
            }
        }
        return count;
    }

    private static void Validate(SubjectDefinition subject)
    {
        if (string.IsNullOrWhiteSpace(subject.Id) || subject.Id.Length > 64)
            throw new ArgumentException("Subject id must contain 1-64 characters.");
        if (subject.Parts.Count is < 3 or > 128)
            throw new ArgumentException("A subject must contain 3-128 body parts.");
        if (subject.Parts.Select(part => part.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != subject.Parts.Count)
            throw new ArgumentException("Body part ids must be unique.");
        var partIds = subject.Parts.Select(part => part.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (BodyPartDefinition part in subject.Parts)
        {
            if (string.IsNullOrWhiteSpace(part.Id) ||
                !double.IsFinite(part.Radius) ||
                part.Radius is < 0.015 or > 0.75)
                throw new ArgumentException($"Body part '{part.Id}' has invalid dimensions.");
            if (!double.IsFinite(part.Mobility) ||
                !double.IsFinite(part.Softness) ||
                part.Mobility is < 0 or > 1 ||
                part.Softness is < 0 or > 1)
                throw new ArgumentException($"Body part '{part.Id}' mobility and softness must be between 0 and 1.");
            if (!double.IsFinite(part.Position.X + part.Position.Y + part.Position.Z))
                throw new ArgumentException($"Body part '{part.Id}' has a non-finite position.");
            if (part.Parent is not null && !partIds.Contains(part.Parent))
                throw new ArgumentException($"Body part '{part.Id}' refers to missing parent '{part.Parent}'.");
        }

        var parents = subject.Parts.ToDictionary(
            part => part.Id,
            part => part.Parent,
            StringComparer.OrdinalIgnoreCase);
        foreach (BodyPartDefinition part in subject.Parts)
        {
            var path = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? current = part.Id;
            while (current is not null)
            {
                if (!path.Add(current))
                    throw new ArgumentException($"Body part hierarchy contains a cycle at '{current}'.");
                current = parents[current];
            }
        }
    }

    private static SubjectDefinition CreateDefault() => new()
    {
        Id = "default_mew",
        DisplayName = "Mew / Standard Research Avatar",
        Description = "Programmatic soft humanoid avatar with head, ears, torso and articulated limbs.",
        Parts =
        [
            new() { Id = "pelvis", Position = new(0, 0.82, 0), Radius = 0.18, Mobility = 0.12, Softness = 0.42 },
            new() { Id = "torso", Position = new(0, 1.16, 0), Radius = 0.25, Mobility = 0.16, Softness = 0.58, Parent = "pelvis" },
            new() { Id = "chest", Position = new(0, 1.40, -0.01), Radius = 0.21, Mobility = 0.22, Softness = 0.62, Parent = "torso" },
            new() { Id = "neck", Position = new(0, 1.58, 0), Radius = 0.09, Mobility = 0.35, Softness = 0.48, Parent = "chest" },
            new() { Id = "head", Position = new(0, 1.76, 0), Radius = 0.17, Mobility = 0.50, Softness = 0.70, Parent = "neck" },
            new() { Id = "left_cheek", Position = new(-0.13, 1.74, -0.08), Radius = 0.085, Mobility = 0.62, Softness = 0.86, Parent = "head" },
            new() { Id = "right_cheek", Position = new(0.13, 1.74, -0.08), Radius = 0.085, Mobility = 0.62, Softness = 0.86, Parent = "head" },
            new() { Id = "left_ear", Position = new(-0.11, 1.96, 0), Radius = 0.095, Mobility = 0.82, Softness = 0.78, Parent = "head" },
            new() { Id = "right_ear", Position = new(0.11, 1.96, 0), Radius = 0.095, Mobility = 0.82, Softness = 0.78, Parent = "head" },
            new() { Id = "left_upper_arm", Position = new(-0.30, 1.37, 0), Radius = 0.10, Mobility = 0.42, Softness = 0.55, Parent = "chest" },
            new() { Id = "left_forearm", Position = new(-0.48, 1.12, -0.02), Radius = 0.085, Mobility = 0.58, Softness = 0.52, Parent = "left_upper_arm" },
            new() { Id = "left_hand", Position = new(-0.51, 0.91, -0.04), Radius = 0.09, Mobility = 0.72, Softness = 0.58, Parent = "left_forearm" },
            new() { Id = "right_upper_arm", Position = new(0.30, 1.37, 0), Radius = 0.10, Mobility = 0.42, Softness = 0.55, Parent = "chest" },
            new() { Id = "right_forearm", Position = new(0.48, 1.12, -0.02), Radius = 0.085, Mobility = 0.58, Softness = 0.52, Parent = "right_upper_arm" },
            new() { Id = "right_hand", Position = new(0.51, 0.91, -0.04), Radius = 0.09, Mobility = 0.72, Softness = 0.58, Parent = "right_forearm" },
            new() { Id = "left_thigh", Position = new(-0.12, 0.52, 0), Radius = 0.13, Mobility = 0.28, Softness = 0.60, Parent = "pelvis" },
            new() { Id = "left_foot", Position = new(-0.13, 0.10, -0.08), Radius = 0.12, Mobility = 0.20, Softness = 0.38, Parent = "left_thigh" },
            new() { Id = "right_thigh", Position = new(0.12, 0.52, 0), Radius = 0.13, Mobility = 0.28, Softness = 0.60, Parent = "pelvis" },
            new() { Id = "right_foot", Position = new(0.13, 0.10, -0.08), Radius = 0.12, Mobility = 0.20, Softness = 0.38, Parent = "right_thigh" },
        ],
    };
}

public static class ResearchCatalog
{
    public static IReadOnlyList<ResearchTaskDefinition> All { get; } =
    [
        new()
        {
            Id = "head_petting",
            DisplayName = "Head Petting",
            Description = "Slow repeated strokes across the crown; rewards continuous soft contact.",
            TargetPart = "head",
            SubjectOffset = new(-2.25, 0, 0.15),
            ApproachPoint = new(0, 1.93, -0.16),
            StrokeAxis = new(1, 0, 0),
            DesiredForce = 0.65,
            PreferredStrokeSpeed = 0.24,
            StrokeSpeedTolerance = 0.14,
            TargetDirectionReversals = 5,
            RecommendedDuration = 3,
            ReactionGain = 0.7,
        },
        new()
        {
            Id = "cheek_nuzzle",
            DisplayName = "Cheek Nuzzle",
            Description = "A compliant cheek leans into the hand without oscillation.",
            TargetPart = "left_cheek",
            SubjectOffset = new(-0.75, 0, 0.15),
            ApproachPoint = new(-0.21, 1.74, -0.16),
            StrokeAxis = new(0, 1, 0),
            DesiredForce = 0.65,
            PreferredStrokeSpeed = 0.16,
            StrokeSpeedTolerance = 0.10,
            TargetDirectionReversals = 3,
            RecommendedDuration = 2.5,
            ReactionGain = 1.15,
        },
        new()
        {
            Id = "limp_support",
            DisplayName = "Soft / Limp Support",
            Description = "Catch and support a yielding forearm while avoiding abrupt acceleration.",
            TargetPart = "left_forearm",
            SubjectOffset = new(0.75, 0, 0.15),
            ApproachPoint = new(-0.48, 1.08, -0.10),
            StrokeAxis = new(0, 0.5, 0.5),
            DesiredForce = 1.4,
            PreferredStrokeSpeed = 0.12,
            StrokeSpeedTolerance = 0.08,
            TargetDirectionReversals = 5,
            RecommendedDuration = 3,
            ReactionGain = 1.65,
        },
        new()
        {
            Id = "hand_hold",
            DisplayName = "Hand Hold",
            Description = "Approach, grip and settle the hand with low residual jitter.",
            TargetPart = "right_hand",
            SubjectOffset = new(2.25, 0, 0.15),
            ApproachPoint = new(0.51, 0.91, -0.12),
            StrokeAxis = new(0, 0, 1),
            DesiredForce = 1.4,
            PreferredStrokeSpeed = 0.08,
            StrokeSpeedTolerance = 0.06,
            TargetDirectionReversals = 1,
            RecommendedDuration = 2.5,
            ReactionGain = 1.25,
        },
    ];

    public static ResearchTaskDefinition Get(string id) =>
        All.FirstOrDefault(task => task.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Unknown research task '{id}'.");
}
