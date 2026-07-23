using OpenMeow;
using OpenMeow.Lab.Domain;
using OpenMeow.Lab.Orchestration;
using OpenMeow.Lab.Simulation;
using OpenMeow.Lab.Subjects;
using OpenMeow.Lab.UI;

namespace OpenMeow.Lab.Verification;

internal static class SelfTest
{
    public static async Task RunAsync()
    {
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        try
        {
            ApplicationConfiguration.Initialize();
            using var form = new MainForm(new ControlTower());
            Assert(form.Text.Contains("OpenMeow Lab", StringComparison.Ordinal), "UI control tower constructs");
        }
        finally
        {
            // Constructing a WinForms control auto-installs its synchronization
            // context. A self-test has no message loop, so restore the prior context
            // before awaiting parallel tuning work.
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        var tower = new ControlTower();
        WorldSnapshot first = tower.Create(new CreateExperimentRequest
        {
            AgentId = "determinism-a",
            Seed = 42,
        });
        WorldSnapshot second = tower.Create(new CreateExperimentRequest
        {
            AgentId = "determinism-b",
            Seed = 42,
        });

        SequenceRequest sequence = CreateSequence(first);
        WorldSnapshot firstResult = tower.RunSequence(first.ExperimentId, sequence);
        WorldSnapshot secondResult = tower.RunSequence(
            second.ExperimentId,
            sequence with { ExpectedRevision = second.Revision });
        Assert(firstResult.Tick == secondResult.Tick, "deterministic tick count");
        Assert(firstResult.Metrics == secondResult.Metrics, "deterministic metrics");
        Assert(
            firstResult.Metrics.PostContactSampledSeconds == 0,
            "ordinary stroke contact gaps do not masquerade as settling");

        WorldSnapshot beforeRejectedSequence = tower.Observe(second.ExperimentId);
        bool invalidSequenceRejected = false;
        try
        {
            tower.RunSequence(second.ExperimentId, new SequenceRequest
            {
                ExpectedRevision = beforeRejectedSequence.Revision,
                Actions =
                [
                    new() { Target = new(0, 1.5, -0.3), DurationSeconds = 0.2 },
                    new() { Target = new(9, 1.5, -0.3), DurationSeconds = 0.2 },
                ],
            });
        }
        catch (ArgumentOutOfRangeException)
        {
            invalidSequenceRejected = true;
        }
        WorldSnapshot afterRejectedSequence = tower.Observe(second.ExperimentId);
        Assert(invalidSequenceRejected, "single-axis out-of-bounds target is rejected");
        Assert(
            afterRejectedSequence.Tick == beforeRejectedSequence.Tick &&
            afterRejectedSequence.Revision == beforeRejectedSequence.Revision &&
            afterRejectedSequence.Metrics == beforeRejectedSequence.Metrics,
            "rejected sequences are atomic");

        bool grippingSettlingRejected = false;
        try
        {
            tower.Act(second.ExperimentId, new ActionRequest
            {
                ExpectedRevision = beforeRejectedSequence.Revision,
                Target = new(0, 1.5, -0.3),
                Grip = true,
                MeasureSettling = true,
            });
        }
        catch (ArgumentException)
        {
            grippingSettlingRejected = true;
        }
        Assert(grippingSettlingRejected, "settling measurement requires a non-gripping retreat");

        bool staleRejected = false;
        try
        {
            tower.Act(first.ExperimentId, new ActionRequest
            {
                ExpectedRevision = 0,
                Target = new(0, 1.5, -0.3),
            });
        }
        catch (StaleRevisionException)
        {
            staleRejected = true;
        }
        Assert(staleRejected, "stale revisions are rejected");

        WorldSnapshot beforeReset = tower.Observe(second.ExperimentId);
        WorldSnapshot afterReset = tower.Reset(second.ExperimentId, beforeReset.Revision);
        Assert(afterReset.Revision == beforeReset.Revision + 1, "reset revision is monotonic");
        Assert(afterReset.Tick == 0, "reset clears simulation time");

        bool invalidModelRejected = false;
        try
        {
            tower.Subjects.Register(new SubjectDefinition
            {
                Id = "invalid_physics",
                DisplayName = "Invalid",
                Parts =
                [
                    new() { Id = "a", Position = default, Mobility = 2 },
                    new() { Id = "b", Position = new(0, 1, 0), Parent = "a" },
                    new() { Id = "c", Position = new(0, 2, 0), Parent = "b" },
                ],
            });
        }
        catch (ArgumentException)
        {
            invalidModelRejected = true;
        }
        Assert(invalidModelRejected, "unsafe model physics values are rejected");

        EvaluationResult evaluation = tower.Evaluate(first.ExperimentId);
        Assert(evaluation.Score is >= 0 and <= 100, "score is bounded");
        Assert(evaluation.Components.Count >= 6, "component scores are present");

        ResearchTaskDefinition task = ResearchCatalog.Get(first.TaskId);
        MetricSnapshot commonMetrics = firstResult.Metrics with
        {
            ElapsedSeconds = 3,
            TargetContactSeconds = 1.5,
            MeanTargetForce = task.DesiredForce,
            TargetForceRmsRelativeError = 0.05,
            MeanTargetContactSpeed = task.PreferredStrokeSpeed,
            PeakTargetContactSpeed = task.PreferredStrokeSpeed * 1.2,
            DirectionReversals = task.TargetDirectionReversals,
            PostContactSampledSeconds = 0.9,
            PostContactRmsSubjectSpeed = 0.08,
            PostContactPeakSubjectSpeed = 0.18,
            PeakTargetForce = task.DesiredForce * 1.2,
        };
        EvaluationResult forceMatched = Evaluator.Evaluate(
            firstResult with { Metrics = commonMetrics }, task, []);
        EvaluationResult forceWeak = Evaluator.Evaluate(
            firstResult with
            {
                Metrics = commonMetrics with
                {
                    MeanTargetForce = task.DesiredForce * 0.12,
                    TargetForceRmsRelativeError = 0.88,
                },
            }, task, []);
        Assert(
            forceWeak.Score < forceMatched.Score * 0.65 &&
            forceWeak.Components["target_force_gate"] < 0.25,
            "weak target force is materially gated");

        EvaluationResult unsettled = Evaluator.Evaluate(
            firstResult with
            {
                Metrics = commonMetrics with
                {
                    PostContactRmsSubjectSpeed = 0.9,
                    PostContactPeakSubjectSpeed = 1.4,
                },
            }, task, []);
        Assert(
            unsettled.Components["reaction_settling"] < forceMatched.Components["reaction_settling"],
            "post-release residual speed lowers settling");

        EvaluationResult fastStroke = Evaluator.Evaluate(
            firstResult with
            {
                Metrics = commonMetrics with
                {
                    MeanTargetContactSpeed = task.PreferredStrokeSpeed * 5,
                    PeakTargetContactSpeed = task.PreferredStrokeSpeed * 5,
                },
            }, task, []);
        Assert(
            fastStroke.Components["stroke_naturalness"] < forceMatched.Components["stroke_naturalness"],
            "excessive stroke speed lowers naturalness");

        EvaluationResult missedContact = Evaluator.Evaluate(
            firstResult with
            {
                Metrics = commonMetrics with
                {
                    TargetContactSeconds = 0,
                    MeanTargetForce = 0,
                    MeanTargetContactSpeed = 0,
                    PeakTargetContactSpeed = 0,
                    DirectionReversals = 0,
                },
            }, task, []);
        Assert(
            missedContact.Components["target_speed_naturalness"] == 0 &&
            missedContact.Components["direction_reversal_naturalness"] == 0 &&
            missedContact.Components["reaction_settling"] == 0,
            "missed contact cannot earn stroke or settling components");

        WorldSnapshot benchmark = tower.Create(new CreateExperimentRequest
        {
            AgentId = "benchmark-release",
            Seed = 11,
        });
        try
        {
            AutoTuner.RunBenchmark(tower, benchmark.ExperimentId);
            Assert(
                tower.Observe(benchmark.ExperimentId).Metrics.PostContactSampledSeconds >= 0.75,
                "benchmark measures post-contact release settling");
        }
        finally
        {
            tower.Remove(benchmark.ExperimentId);
        }

        TuneResult tuned = await tower.AutoTuneAsync(new TuneRequest
        {
            Candidates = 4,
            Parallelism = 2,
            Seed = 7,
        });
        Assert(tuned.CandidatesEvaluated == 4, "auto-tuner evaluates every candidate");
        Assert(tuned.BestScore is >= 0 and <= 100, "auto-tuner result is bounded");
        TuneResult tunedAgain = await tower.AutoTuneAsync(new TuneRequest
        {
            Candidates = 4,
            Parallelism = 2,
            Seed = 7,
        });
        Assert(
            tunedAgain.BestProfile.Name == tuned.BestProfile.Name &&
            tunedAgain.BestScore == tuned.BestScore,
            "parallel auto-tuning has a deterministic winner");

        GaitBenchmarkResult gait = tower.RunGaitBenchmark(new GaitBenchmarkRequest { Seed = 31 });
        GaitBenchmarkResult gaitAgain = tower.RunGaitBenchmark(new GaitBenchmarkRequest { Seed = 31 });
        Assert(JsonTransport.Serialize(gait) == JsonTransport.Serialize(gaitAgain), "gait benchmark is deterministic");
        Assert(gait.Samples.Count == 810, "gait benchmark uses the six fixed 90 Hz durations");
        Assert(gait.Samples.All(sample => sample.IsFinite), "gait poses remain finite");
        Assert(gait.Metrics.SwingClearanceMeters > 0.02, "gait produces nonzero swing clearance");
        Assert(gait.Metrics.PlantedFootWorldSlipMetersPerSecond < 0.15, "gait planted slip remains bounded");
        Assert(gait.Metrics.ToeTurnAlignmentDegrees < 35, "turn-in-place responds with toe yaw");
        Assert(gait.Metrics.StopSettlingSpeed < 0.35, "gait stop settles to low residual speed");
        Assert(double.IsFinite(gait.Metrics.MovementSeconds) && gait.Metrics.MovementSeconds > 0, "gait movement seconds are finite");
        double gaitCadence = gait.Metrics.SwingSteps / gait.Metrics.MovementSeconds;
        Assert(gaitCadence is >= 1.2 and <= 2.8, "Natural gait cadence is in the expected human step band");
        Assert(double.IsFinite(gait.Components["cadence_naturalness"]), "gait cadence component is finite");
        Assert(gait.Score is >= 0 and <= 100, "gait score is bounded");
        Assert(gait.Components["evaluator_version"] == 8, "gait evaluator version is v8");
        Assert(gait.Components.Where(pair => pair.Key != "evaluator_version").Select(pair => pair.Value).All(value => value is >= 0 and <= 1),
            "gait evaluator score components are bounded");
        GaitProfile gaitDefaults = new GaitProfile();
        Assert(
            gaitDefaults.BodyHeightMeters == 1.65 && gaitDefaults.HipFollowTau == .08 &&
            gaitDefaults.HipLeanDegrees == 11 && gaitDefaults.FootSpacingMeters == .20 &&
            gaitDefaults.StrideLengthMeters == .45 && gaitDefaults.StepHeightMeters == .06 &&
            gaitDefaults.GaitSmoothingTau == .24 && gaitDefaults.TurnToeDegrees == 7 &&
            gaitDefaults.FootPlantStrength == .92,
            "gait defaults match Natural desktop preset");
        GaitProfile gaitSafe = new GaitProfile()
        {
            BodyHeightMeters = double.NaN,
            HipFollowTau = double.PositiveInfinity,
            HipLeanDegrees = -4,
            FootSpacingMeters = 9,
            StrideLengthMeters = 9,
            StepHeightMeters = -1,
            GaitSmoothingTau = 0,
            TurnToeDegrees = 99,
        }.Sanitize();
        Assert(gaitSafe.BodyHeightMeters == 1.65 && gaitSafe.HipFollowTau == .08 && gaitSafe.HipLeanDegrees == 0 &&
               gaitSafe.FootSpacingMeters == .45 && gaitSafe.StrideLengthMeters == 1.2 && gaitSafe.StepHeightMeters == 0 &&
               gaitSafe.GaitSmoothingTau == .02 && gaitSafe.TurnToeDegrees == 35,
            "gait sanitize uses runtime-safe Natural ranges");
        GaitSimulator idle = new();
        idle.Run([new GaitScenarioSegment(GaitCommand.Idle, 1)]);
        Assert(idle.Metrics.ActualTravelMeters < 0.001, "idle gait has low drift");
        Assert(idle.Metrics.StopSettlingSpeed == 0, "idle samples are excluded from stop settling");
        Assert(gait.Metrics.StopSettlingSpeed > 0, "stop segment contributes stop settling metrics");
        GaitBenchmarkResult turnOnly = tower.RunGaitBenchmark(new GaitBenchmarkRequest
        {
            Scenario =
            [
                new GaitScenarioSegment(GaitCommand.Idle, .5),
                new GaitScenarioSegment(GaitCommand.TurnInPlace, 2),
                new GaitScenarioSegment(GaitCommand.Stop, 1),
            ],
        });
        Assert(turnOnly.Metrics.TurnDegrees > 10 && turnOnly.Score > 0 &&
               turnOnly.Components["movement_coverage"] > 0,
            "turn-only gait counts rotational motion as movement coverage");
        GaitBenchmarkResult normalSpeed = tower.RunGaitBenchmark(new GaitBenchmarkRequest
        {
            Scenario = [new GaitScenarioSegment(GaitCommand.Forward, 1)],
        });
        GaitBenchmarkResult fastSpeed = tower.RunGaitBenchmark(new GaitBenchmarkRequest
        {
            Scenario = [new GaitScenarioSegment(GaitCommand.Forward, 1, 2.25)],
        });
        Assert(fastSpeed.Metrics.CommandedTravelMeters > normalSpeed.Metrics.CommandedTravelMeters * 2 &&
               new GaitScenarioSegment(GaitCommand.Forward, 1, double.NaN).Sanitize().SpeedMultiplier == 1 &&
               new GaitScenarioSegment(GaitCommand.Forward, 1, 99).Sanitize().SpeedMultiplier == 3,
            "gait scenario speed multiplier models Driver slow/normal/fast input safely");

        BodyGaitParameters kernelParameters = BodyGaitParameters.Natural;
        BodyGaitHeadPose[] kernelTrace =
        [
            new(0, kernelParameters.BodyHeightMeters, 0, 0),
            new(0, kernelParameters.BodyHeightMeters, -.02, 0),
            new(.01, kernelParameters.BodyHeightMeters, -.04, .01),
            new(.02, kernelParameters.BodyHeightMeters, -.06, .02),
        ];
        var kernelReplayA = new BodyGaitKernel();
        var kernelReplayB = new BodyGaitKernel();
        kernelReplayA.Configure(kernelParameters);
        kernelReplayB.Configure(kernelParameters);
        foreach (BodyGaitHeadPose head in kernelTrace)
        {
            BodyGaitFrame a = kernelReplayA.Step(GaitSimulator.TickSeconds, head);
            BodyGaitFrame b = kernelReplayB.Step(GaitSimulator.TickSeconds, head);
            Assert(a.Waist.X == b.Waist.X && a.Waist.Y == b.Waist.Y && a.Waist.Z == b.Waist.Z &&
                   a.LeftFoot.X == b.LeftFoot.X && a.LeftFoot.Y == b.LeftFoot.Y &&
                   a.RightFoot.X == b.RightFoot.X && a.RightFoot.Y == b.RightFoot.Y &&
                   a.Phase == b.Phase,
                "shared gait kernel replay is deterministic");
            Assert(IsFinite(a), "shared gait kernel output remains finite");
        }
        var idleKernel = new BodyGaitKernel();
        idleKernel.Configure(kernelParameters);
        BodyGaitFrame idleFrame = BodyGaitFrame.Default;
        for (int i = 0; i < 90; i++)
            idleFrame = idleKernel.Step(GaitSimulator.TickSeconds,
                new BodyGaitHeadPose(0, kernelParameters.BodyHeightMeters, 0, 0));
        Assert(Math.Abs(idleFrame.Waist.X) < 0.001 && Math.Abs(idleFrame.Waist.Z) < 0.001,
            "shared gait kernel idle has low drift");
        var calibratedKernel = new BodyGaitKernel();
        calibratedKernel.Configure(kernelParameters);
        calibratedKernel.Reset(new BodyGaitHeadPose(0, 1.60, 0, 0));
        BodyGaitFrame calibratedFrame = calibratedKernel.Step(
            GaitSimulator.TickSeconds, new BodyGaitHeadPose(0, 1.60, 0, 0));
        Assert(calibratedFrame.LeftPlanted && calibratedFrame.RightPlanted &&
               Math.Abs(calibratedFrame.LeftFoot.Y - .025) < 1e-9 &&
               Math.Abs(calibratedFrame.RightFoot.Y - .025) < 1e-9,
            "shared gait kernel calibrates floor height and starts with both feet planted");
        BodyGaitFrame turnFrameA = calibratedKernel.Step(
            GaitSimulator.TickSeconds, new BodyGaitHeadPose(0, 1.60, 0, .01));
        BodyGaitFrame turnFrameB = calibratedKernel.Step(
            GaitSimulator.TickSeconds, new BodyGaitHeadPose(0, 1.60, 0, .02));
        double expectedWaistYawVelocity =
            (turnFrameB.Waist.Yaw - turnFrameA.Waist.Yaw) / GaitSimulator.TickSeconds;
        Assert(Math.Abs(turnFrameB.Waist.AngularVelocityYaw - expectedWaistYawVelocity) < 1e-9 &&
               Math.Abs(turnFrameB.LeftFoot.AngularVelocityYaw) > 1e-6 &&
               Math.Abs(turnFrameB.RightFoot.AngularVelocityYaw) > 1e-6,
            "shared gait kernel reports tracker-specific yaw velocity");
        BodyGaitFrame invalidKernelFrame = idleKernel.Step(double.NaN,
            new BodyGaitHeadPose(double.NaN, double.PositiveInfinity, double.NaN, double.NaN));
        Assert(IsFinite(invalidKernelFrame), "shared gait kernel sanitizes invalid input");
        GaitTuneResult gaitTune = await tower.AutoTuneGaitAsync(new GaitAutotuneRequest
        {
            Seed = 9,
            Candidates = 4,
            Parallelism = 2,
        });
        GaitTuneResult gaitTuneAgain = await tower.AutoTuneGaitAsync(new GaitAutotuneRequest
        {
            Seed = 9,
            Candidates = 4,
            Parallelism = 2,
        });
        Assert(gaitTune.CandidatesEvaluated == 4, "gait autotuner evaluates every candidate");
        Assert(gaitTune.BestScore is >= 0 and <= 100, "gait autotuner score is bounded");
        Assert(gaitTune.BestProfile == gaitTuneAgain.BestProfile && gaitTune.BestScore == gaitTuneAgain.BestScore,
            "parallel gait tuning has a deterministic winner");
        Assert(gaitTune.TopResults.All(result =>
            result.Profile.BodyHeightMeters == new GaitProfile().BodyHeightMeters &&
            result.Profile.FootSpacingMeters == new GaitProfile().FootSpacingMeters),
            "gait autotune holds calibrated body height and foot spacing fixed");
        GaitBenchmarkResult shuffle = tower.RunGaitBenchmark(new GaitBenchmarkRequest
        {
            Seed = 31,
            Profile = new GaitProfile { StrideLengthMeters = .10, FootPlantStrength = 1 },
        });
        Assert(shuffle.Score < gait.Score, "pathological shuffle profile cannot exploit the gait reward");
        Assert(shuffle.Metrics.SwingSteps / shuffle.Metrics.MovementSeconds > gaitCadence,
            "short shuffle stride produces an excessive cadence");
        GaitEvaluationResult phaseBalanced = GaitEvaluator.Evaluate(
            gait.Profile,
            gait.Metrics with { LeftRightPhaseAsymmetry = 0 });
        GaitEvaluationResult phaseAsymmetric = GaitEvaluator.Evaluate(
            gait.Profile,
            gait.Metrics with { LeftRightPhaseAsymmetry = 1 });
        Assert(phaseAsymmetric.Score < phaseBalanced.Score, "measured phase asymmetry lowers score");
        GaitEvaluationResult nonFinite = GaitEvaluator.Evaluate(
            gait.Profile,
            gait.Metrics with { WaistPeakJerk = double.NaN, MovementSeconds = double.NaN });
        Assert(nonFinite.Score == 0 && nonFinite.Components.Values.All(double.IsFinite),
            "non-finite gait metrics produce finite zero score/components");
        Assert(double.IsFinite(nonFinite.Metrics.WaistPeakJerk) && double.IsFinite(nonFinite.Metrics.MovementSeconds),
            "non-finite gait metrics are sanitized for JSON");
        Assert(JsonTransport.Serialize(nonFinite).Length > 0, "non-finite gait evaluation is JSON serializable");
        GaitEvaluationResult negativeMetrics = GaitEvaluator.Evaluate(
            gait.Profile,
            gait.Metrics with { PlantedFootWorldSlipMetersPerSecond = -1 });
        Assert(negativeMetrics.Score == 0 &&
               negativeMetrics.Metrics.PlantedFootWorldSlipMetersPerSecond == 0,
            "negative gait metrics are invalid and cannot inflate reward");
        GaitBenchmarkResult plantNatural = tower.RunGaitBenchmark(new GaitBenchmarkRequest
        {
            Seed = 31,
            Profile = gait.Profile with { FootPlantStrength = .90 },
        });
        GaitBenchmarkResult plantFixed = tower.RunGaitBenchmark(new GaitBenchmarkRequest
        {
            Seed = 31,
            Profile = gait.Profile with { FootPlantStrength = 1.0 },
        });
        Assert(plantNatural.Score > plantFixed.Score, "natural plant compliance beats fully fixed planting");
        bool oversizedRejected = false;
        try
        {
            tower.RunGaitBenchmark(new GaitBenchmarkRequest
            {
                Scenario = Enumerable.Repeat(new GaitScenarioSegment(GaitCommand.Idle, .01), 129).ToArray(),
            });
        }
        catch (ArgumentOutOfRangeException)
        {
            oversizedRejected = true;
        }
        Assert(oversizedRejected, "oversized gait scenarios are rejected");
        bool longScenarioRejected = false;
        try
        {
            tower.RunGaitBenchmark(new GaitBenchmarkRequest
            {
                Scenario = Enumerable.Repeat(new GaitScenarioSegment(GaitCommand.Idle, 30), 5).ToArray(),
            });
        }
        catch (ArgumentOutOfRangeException)
        {
            longScenarioRejected = true;
        }
        Assert(longScenarioRejected, "gait scenarios over 120 seconds are rejected");
        bool nullSegmentRejected = false;
        try
        {
            tower.RunGaitBenchmark(new GaitBenchmarkRequest
            {
                Scenario = new GaitScenarioSegment[] { null! },
            });
        }
        catch (ArgumentException)
        {
            nullSegmentRejected = true;
        }
        Assert(nullSegmentRejected, "null gait scenario segments are rejected");
        using var cancelledGait = new CancellationTokenSource();
        cancelledGait.Cancel();
        bool cancellationRejected = false;
        try
        {
            tower.RunGaitBenchmark(new GaitBenchmarkRequest(), cancelledGait.Token);
        }
        catch (OperationCanceledException)
        {
            cancellationRejected = true;
        }
        Assert(cancellationRejected, "cancelled gait benchmark stops before simulation");

        GaitProfile bridgeProfile = new()
        {
            BodyHeightMeters = 1.8,
            HipFollowTau = .31,
            HipLeanDegrees = 12,
            FootSpacingMeters = .27,
            StrideLengthMeters = .67,
            StepHeightMeters = .13,
            GaitSmoothingTau = .21,
            TurnToeDegrees = 19,
            FootPlantStrength = .73,
        };
        GaitDriverProfileBridgeResult gaitPreview = tower.PreviewGaitDriverProfile(bridgeProfile, "Natural");
        Assert(!gaitPreview.Applied && !gaitPreview.RestartRequired, "gait preview is read-only");
        Assert(gaitPreview.Settings.BodyHeightMeters == bridgeProfile.BodyHeightMeters &&
               gaitPreview.Settings.HipFollowTau == bridgeProfile.HipFollowTau &&
               gaitPreview.Settings.HipLeanDegrees == bridgeProfile.HipLeanDegrees &&
               gaitPreview.Settings.FootSpacingMeters == bridgeProfile.FootSpacingMeters &&
               gaitPreview.Settings.StrideLengthMeters == bridgeProfile.StrideLengthMeters &&
               gaitPreview.Settings.StepHeightMeters == bridgeProfile.StepHeightMeters &&
               gaitPreview.Settings.GaitSmoothingTau == bridgeProfile.GaitSmoothingTau &&
               gaitPreview.Settings.TurnToeDegrees == bridgeProfile.TurnToeDegrees &&
               gaitPreview.Settings.FootPlantStrength == bridgeProfile.FootPlantStrength,
            "gait preview maps all nine fields");

        MotionProfile compact = tower.RecommendBindings("compact");
        Assert(compact.Bindings["right_hand_move"].Contains("Mouse4"), "compact binding preset");

        MotionProfile researchProfile = new()
        {
            Name = "pilot-natural",
            PositionSpringHz = 6.75,
            DampingRatio = 0.72,
            MaxSpeed = 1.55,
            MaxAcceleration = 14.0,
            PredictionSeconds = 0.028,
            ContactCompliance = 0.15,
            HandRadius = 0.14,
        };
        DriverProfileBridgeResult preview = tower.PreviewDriverProfile(researchProfile, "Natural");
        DriverProfileBridgeResult previewAgain = tower.PreviewDriverProfile(researchProfile, "Natural");
        Assert(
            JsonTransport.Serialize(preview) == JsonTransport.Serialize(previewAgain),
            "driver profile preview is deterministic");
        Assert(preview.Settings.HandSmoothingMode == OpenMeow.MotionSmoothingMode.SecondOrder, "driver preview uses second-order smoothing");
        Assert(preview.Settings.HandDamping >= 1.0, "driver preview respects damping floor");
        Assert(Math.Abs(preview.Settings.HandSpringHz - researchProfile.PositionSpringHz) < 1e-9, "driver preview maps spring frequency");
        DesktopMotionSettings natural = DesktopMotionSettings.PresetOrLegacy("Natural");
        Assert(preview.Settings.MovementSpeed == natural.MovementSpeed, "driver preview preserves base preset fields");
        Assert(!preview.Applied, "driver preview does not apply settings");
        DesktopMotionSettings currentDesktop = DesktopMotionSettings.LoadOrDefault();
        Assert(preview.Settings.EnableBodyTrackers == currentDesktop.EnableBodyTrackers &&
               preview.Settings.BodyHeightMeters == currentDesktop.BodyHeightMeters &&
               preview.Settings.HipFollowTau == currentDesktop.HipFollowTau &&
               preview.Settings.HipLeanDegrees == currentDesktop.HipLeanDegrees &&
               preview.Settings.FootSpacingMeters == currentDesktop.FootSpacingMeters &&
               preview.Settings.StrideLengthMeters == currentDesktop.StrideLengthMeters &&
               preview.Settings.StepHeightMeters == currentDesktop.StepHeightMeters &&
               preview.Settings.GaitSmoothingTau == currentDesktop.GaitSmoothingTau &&
               preview.Settings.TurnToeDegrees == currentDesktop.TurnToeDegrees &&
               preview.Settings.FootPlantStrength == currentDesktop.FootPlantStrength,
            "hand preview preserves current topology and gait settings");

        Console.WriteLine(
            $"Self-test passed: deterministic simulation, revision isolation, evaluation, " +
            $"parallel tuning ({tuned.BestScore:F2}).");
    }

    private static SequenceRequest CreateSequence(WorldSnapshot snapshot)
    {
        BodyPartSnapshot head = snapshot.Parts.First(part => part.Id == "head");
        double z = head.Position.Z - head.Radius - snapshot.Profile.HandRadius + 0.035;
        return new SequenceRequest
        {
            ExpectedRevision = snapshot.Revision,
            Actions =
            [
                new() { Target = new(head.Position.X, head.Position.Y + 0.1, z - 0.2), DurationSeconds = 0.4 },
                new() { Target = new(head.Position.X - 0.1, head.Position.Y + 0.08, z), DurationSeconds = 0.5 },
                new() { Target = new(head.Position.X + 0.1, head.Position.Y + 0.08, z), DurationSeconds = 0.5 },
                new() { Target = new(head.Position.X, head.Position.Y + 0.08, z), DurationSeconds = 0.5 },
            ],
        };
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"Self-test failed: {message}.");
    }

    private static bool IsFinite(BodyGaitFrame frame) =>
        IsFinite(frame.Waist) && IsFinite(frame.LeftFoot) && IsFinite(frame.RightFoot) &&
        double.IsFinite(frame.YawRate) && double.IsFinite(frame.Phase) &&
        double.IsFinite(frame.HorizontalSpeed);

    private static bool IsFinite(BodyGaitTrackerPose pose) =>
        double.IsFinite(pose.X) && double.IsFinite(pose.Y) && double.IsFinite(pose.Z) &&
        double.IsFinite(pose.Vx) && double.IsFinite(pose.Vy) && double.IsFinite(pose.Vz) &&
        double.IsFinite(pose.Yaw) && double.IsFinite(pose.Pitch) && double.IsFinite(pose.Roll) &&
        double.IsFinite(pose.AngularVelocityYaw);
}
