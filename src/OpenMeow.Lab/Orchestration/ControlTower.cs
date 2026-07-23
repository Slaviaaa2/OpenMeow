using System.Collections.Concurrent;
using OpenMeow.Lab.Domain;
using OpenMeow.Lab.Simulation;
using OpenMeow.Lab.Subjects;

namespace OpenMeow.Lab.Orchestration;

public sealed class ControlTower
{
    private readonly ConcurrentDictionary<Guid, ResearchWorld> _experiments = new();
    private readonly ConcurrentDictionary<Guid, object> _mutationGates = new();

    public ControlTower(string? modelDirectory = null)
    {
        Subjects = new SubjectRegistry(modelDirectory);
    }

    public SubjectRegistry Subjects { get; }
    public IReadOnlyList<ResearchTaskDefinition> Tasks => ResearchCatalog.All;

    public WorldSnapshot Create(CreateExperimentRequest request)
    {
        SubjectDefinition subject = Subjects.Get(request.SubjectId);
        ResearchTaskDefinition task = ResearchCatalog.Get(request.TaskId);
        if (!subject.Parts.Any(part =>
                part.Id.Equals(task.TargetPart, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException(
                $"Subject '{subject.Id}' does not contain required target part '{task.TargetPart}'.");
        Guid id = Guid.NewGuid();
        var world = new ResearchWorld(
            id,
            NormalizeAgentId(request.AgentId),
            subject,
            task,
            request.Profile ?? new MotionProfile(),
            request.Seed);
        if (!_experiments.TryAdd(id, world))
            throw new InvalidOperationException("Could not allocate an experiment.");
        if (!_mutationGates.TryAdd(id, new object()))
        {
            _experiments.TryRemove(id, out _);
            throw new InvalidOperationException("Could not allocate an experiment gate.");
        }
        return world.Observe();
    }

    public IReadOnlyList<WorldSnapshot> List() =>
        _experiments.Values
            .Select(world => world.Observe())
            .OrderBy(snapshot => snapshot.AgentId)
            .ThenBy(snapshot => snapshot.ExperimentId)
            .ToArray();

    public WorldSnapshot Observe(Guid id) => Get(id).Observe();
    public WorldSnapshot Act(Guid id, ActionRequest request) => Mutate(id, world => world.Act(request));
    public WorldSnapshot RunSequence(Guid id, SequenceRequest request) =>
        Mutate(id, world => world.RunSequence(request));
    public EvaluationResult Evaluate(Guid id) => Get(id).Evaluate();
    public WorldSnapshot SetProfile(Guid id, MotionProfile profile, long? expectedRevision = null) =>
        Mutate(id, world => world.SetProfile(profile, expectedRevision));

    public WorldSnapshot Reset(Guid id, long? expectedRevision = null)
    {
        object gate = GetMutationGate(id);
        lock (gate)
        {
            ResearchWorld current = Get(id);
            WorldSnapshot snapshot = current.Observe();
            if (expectedRevision.HasValue && expectedRevision.Value != snapshot.Revision)
                throw new StaleRevisionException(expectedRevision.Value, snapshot.Revision);
            SubjectDefinition subject = Subjects.Get(snapshot.SubjectId);
            ResearchTaskDefinition task = ResearchCatalog.Get(snapshot.TaskId);
            var replacement = new ResearchWorld(
                id,
                snapshot.AgentId,
                subject,
                task,
                snapshot.Profile,
                current.Seed,
                snapshot.Revision + 1);
            if (!_experiments.TryUpdate(id, replacement, current))
                throw new InvalidOperationException("The experiment changed while it was being reset.");
            return replacement.Observe();
        }
    }

    public bool Remove(Guid id)
    {
        if (!_mutationGates.TryGetValue(id, out object? gate)) return false;
        lock (gate)
        {
            bool removed = _experiments.TryRemove(id, out _);
            _mutationGates.TryRemove(id, out _);
            return removed;
        }
    }

    public ComparisonResult Compare(IEnumerable<Guid> ids)
    {
        EvaluationResult[] ranking = ids
            .Distinct()
            .Select(Evaluate)
            .OrderByDescending(result => result.Score)
            .ToArray();
        if (ranking.Length < 2)
            throw new ArgumentException("Comparison requires at least two experiments.");

        EvaluationResult winner = ranking[0];
        EvaluationResult runnerUp = ranking[1];
        return new ComparisonResult(
            winner.ExperimentId,
            ranking,
            $"{winner.ExperimentId} leads by {winner.Score - runnerUp.Score:F2} points.");
    }

    public Task<TuneResult> AutoTuneAsync(TuneRequest request, CancellationToken cancellationToken = default) =>
        AutoTuner.RunAsync(this, request, cancellationToken);

    /// <summary>Runs the stateless full-body gait benchmark at 90 Hz.</summary>
    public GaitBenchmarkResult RunGaitBenchmark(
        GaitBenchmarkRequest? request = null,
        CancellationToken cancellationToken = default) =>
        GaitResearch.RunBenchmark(request, cancellationToken);

    public GaitEvaluationResult EvaluateGait(GaitBenchmarkResult result) =>
        GaitResearch.Evaluate(result);

    public Task<GaitTuneResult> AutoTuneGaitAsync(
        GaitAutotuneRequest? request = null,
        CancellationToken cancellationToken = default) =>
        GaitResearch.AutoTuneAsync(request, cancellationToken);

    public GaitTuneResult AutoTuneGait(GaitAutotuneRequest? request = null) =>
        AutoTuneGaitAsync(request).GetAwaiter().GetResult();

    public MotionProfile RecommendBindings(string layout, MotionProfile? baseline = null)
    {
        MotionProfile source = (baseline ?? new MotionProfile()).Sanitize();
        var bindings = new Dictionary<string, string>(source.Bindings, StringComparer.OrdinalIgnoreCase);
        switch (layout.Trim().ToLowerInvariant())
        {
            case "left_handed":
                bindings["right_hand_move"] = "LeftCtrl+Mouse";
                bindings["right_arm_tilt"] = "LeftCtrl+MiddleMouse";
                bindings["left_hand_move"] = "LeftShift+Mouse";
                bindings["left_arm_tilt"] = "LeftShift+MiddleMouse";
                break;
            case "compact":
                bindings["right_hand_move"] = "Mouse4+Mouse";
                bindings["right_arm_tilt"] = "Mouse4+MiddleMouse";
                bindings["left_hand_move"] = "Mouse5+Mouse";
                bindings["left_arm_tilt"] = "Mouse5+MiddleMouse";
                break;
            case "right_handed":
            case "default":
                break;
            default:
                throw new ArgumentException("Layout must be default, right_handed, left_handed or compact.");
        }

        return source with
        {
            Name = $"{source.Name}-{layout}",
            Bindings = bindings,
        };
    }

    public DriverProfileBridgeResult PreviewDriverProfile(MotionProfile profile, string? basePreset = null) =>
        DriverProfileBridge.Preview(profile, basePreset);

    public DriverProfileBridgeResult ApplyDriverProfile(MotionProfile profile, string? basePreset = null) =>
        DriverProfileBridge.Apply(profile, basePreset);

    public GaitDriverProfileBridgeResult PreviewGaitDriverProfile(GaitProfile profile, string? basePreset = null) =>
        GaitDriverProfileBridge.Preview(profile, basePreset);

    public GaitDriverProfileBridgeResult ApplyGaitDriverProfile(
        GaitProfile profile,
        string? basePreset,
        bool enableBodyTrackers) =>
        GaitDriverProfileBridge.Apply(profile, basePreset, enableBodyTrackers);

    private ResearchWorld Get(Guid id) =>
        _experiments.TryGetValue(id, out ResearchWorld? world)
            ? world
            : throw new KeyNotFoundException($"Unknown experiment '{id}'.");

    private T Mutate<T>(Guid id, Func<ResearchWorld, T> mutation)
    {
        object gate = GetMutationGate(id);
        lock (gate)
            return mutation(Get(id));
    }

    private object GetMutationGate(Guid id) =>
        _mutationGates.TryGetValue(id, out object? gate)
            ? gate
            : throw new KeyNotFoundException($"Unknown experiment '{id}'.");

    private static string NormalizeAgentId(string value)
    {
        string result = string.IsNullOrWhiteSpace(value) ? "anonymous" : value.Trim();
        if (result.Length > 64) result = result[..64];
        return result;
    }
}

internal static class AutoTuner
{
    public static async Task<TuneResult> RunAsync(
        ControlTower tower,
        TuneRequest request,
        CancellationToken cancellationToken)
    {
        int candidateCount = Math.Clamp(request.Candidates, 2, 128);
        int parallelism = Math.Clamp(request.Parallelism, 1, 12);
        MotionProfile baseline = (request.Baseline ?? new MotionProfile()).Sanitize();
        MotionProfile[] candidates = CreateCandidates(baseline, request.Seed, candidateCount);
        var results = new ConcurrentBag<EvaluationResult>();

        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = cancellationToken,
            },
            (profile, token) =>
            {
                token.ThrowIfCancellationRequested();
                WorldSnapshot created = tower.Create(new CreateExperimentRequest
                {
                    AgentId = $"autotune-{profile.Name}",
                    SubjectId = request.SubjectId,
                    TaskId = request.TaskId,
                    Seed = request.Seed,
                    Profile = profile,
                });
                try
                {
                    RunBenchmark(tower, created.ExperimentId);
                    results.Add(tower.Evaluate(created.ExperimentId));
                }
                finally
                {
                    tower.Remove(created.ExperimentId);
                }
                return ValueTask.CompletedTask;
            });

        EvaluationResult[] ranking = results
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Profile.Name, StringComparer.Ordinal)
            .ToArray();
        if (ranking.Length == 0)
            throw new InvalidOperationException("Auto-tuning produced no result.");
        return new TuneResult(
            ranking[0].Profile,
            ranking[0].Score,
            ranking.Length,
            ranking.Take(5).ToArray());
    }

    internal static void RunBenchmark(ControlTower tower, Guid experimentId)
    {
        WorldSnapshot snapshot = tower.Observe(experimentId);
        ResearchTaskDefinition task = ResearchCatalog.Get(snapshot.TaskId);
        BodyPartSnapshot targetPart = snapshot.Parts.First(part =>
            part.Id.Equals(task.TargetPart, StringComparison.OrdinalIgnoreCase));
        Vec3 normal = new(0, 0, -1);
        double contactDistance = targetPart.Radius + snapshot.Profile.HandRadius - 0.035;
        Vec3 center = targetPart.Position + normal * contactDistance;
        Vec3 axis = task.StrokeAxis.Normalized();
        if (axis.Length < 0.1) axis = new Vec3(1, 0, 0);

        var actions = new List<ActionRequest>
        {
            new() { Hand = HandSide.Right, Target = center + normal * 0.24, DurationSeconds = 0.55, Label = "approach" },
            new() { Hand = HandSide.Right, Target = center, DurationSeconds = 0.55, Label = "soft-contact" },
        };
        int strokes = task.Id == "hand_hold" ? 2 : 6;
        for (int i = 0; i < strokes; i++)
        {
            double direction = i % 2 == 0 ? -1 : 1;
            // The old fixed 0.34 s timing moved 22 cm at roughly 0.65 m/s,
            // making every gentle task fail its own preferred-speed band.
            // Derive timing from task intent; the first half-stroke travels
            // only 11 cm from center, subsequent reversals travel 22 cm.
            double strokeDistance = i == 0 ? 0.11 : 0.22;
            double strokeDuration = Math.Clamp(
                strokeDistance / Math.Max(task.PreferredStrokeSpeed, 0.04),
                0.25,
                2.75);
            actions.Add(new ActionRequest
            {
                Hand = HandSide.Right,
                Target = center + axis * (0.11 * direction),
                DurationSeconds = strokeDuration,
                Grip = task.Id == "hand_hold",
                Label = "stroke",
            });
        }
        actions.Add(new ActionRequest
        {
            Hand = HandSide.Right,
            Target = center,
            DurationSeconds = 0.65,
            Grip = task.Id == "hand_hold",
            Label = "settle",
        });
        // End every benchmark with a non-gripping retreat. This gives the
        // subject a deterministic 0.9s release window for post-contact settling
        // telemetry instead of allowing an unmeasured final contact to score as
        // perfectly settled.
        actions.Add(new ActionRequest
        {
            Hand = HandSide.Right,
            Target = center + normal * 0.30,
            DurationSeconds = 1.05,
            Grip = false,
            MeasureSettling = true,
            Label = "release-retreat",
        });

        tower.RunSequence(experimentId, new SequenceRequest
        {
            ExpectedRevision = snapshot.Revision,
            Actions = actions,
        });
    }

    private static MotionProfile[] CreateCandidates(MotionProfile baseline, int seed, int count)
    {
        var random = new Random(seed);
        var profiles = new MotionProfile[count];
        profiles[0] = baseline with { Name = "baseline" };
        for (int i = 1; i < count; i++)
        {
            double Scale(double range) => Math.Exp((random.NextDouble() * 2 - 1) * range);
            profiles[i] = (baseline with
            {
                Name = $"candidate-{i:000}",
                PositionSpringHz = baseline.PositionSpringHz * Scale(0.55),
                DampingRatio = baseline.DampingRatio * Scale(0.42),
                MaxSpeed = baseline.MaxSpeed * Scale(0.5),
                MaxAcceleration = baseline.MaxAcceleration * Scale(0.55),
                ContactCompliance = Math.Clamp(
                    baseline.ContactCompliance + (random.NextDouble() * 2 - 1) * 0.28, 0, 1),
                PredictionSeconds = Math.Clamp(
                    baseline.PredictionSeconds + (random.NextDouble() * 2 - 1) * 0.04, 0, 0.2),
            }).Sanitize();
        }
        return profiles;
    }
}
