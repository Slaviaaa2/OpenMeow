using System.Text.Json;
using OpenMeow.Lab.Domain;
using OpenMeow.Lab.Orchestration;
using OpenMeow.Lab.Simulation;

namespace OpenMeow.Lab;

/// <summary>
/// Newline-delimited JSON-RPC 2.0 transport for MCP-compatible local clients.
/// Protocol data is written only to the supplied output writer; diagnostics go to stderr.
/// </summary>
public sealed class McpStdioServer
{
    private static readonly JsonElement EmptyArguments =
        JsonSerializer.SerializeToElement(new { }, JsonTransport.Options);

    private readonly ControlTower _tower;
    private readonly TextWriter _diagnostics;

    public McpStdioServer(ControlTower tower, TextWriter? diagnostics = null)
    {
        _tower = tower ?? throw new ArgumentNullException(nameof(tower));
        _diagnostics = diagnostics ?? Console.Error;
    }

    public Task RunAsync(CancellationToken cancellationToken = default) =>
        RunAsync(Console.In, Console.Out, cancellationToken);

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Length > 1 * 1024 * 1024)
            {
                await WriteErrorAsync(output, JsonTransport.NullId, -32700, "JSON-RPC message exceeds the 1 MiB limit.", null, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            JsonRpcRequest request;
            try
            {
                request = ParseRequest(line);
            }
            catch (JsonException ex)
            {
                Log($"parse error: {ex.Message}");
                await WriteErrorAsync(output, JsonTransport.NullId, -32700, "Parse error.", null, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }
            catch (RpcException ex)
            {
                if (!requestHasId(line)) continue;
                await WriteErrorAsync(output, JsonTransport.NullId, ex.Code, ex.Message, ex.Payload, cancellationToken)
                .ConfigureAwait(false);
                continue;
            }

            try
            {
                if (request.Method.StartsWith("notifications/", StringComparison.Ordinal))
                {
                    await HandleNotificationAsync(request, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                object result = await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
                if (request.HasId)
                    await WriteResultAsync(output, request.Id, result, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (RpcException ex)
            {
                if (request.HasId)
                    await WriteErrorAsync(output, request.Id, ex.Code, ex.Message, ex.Payload, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"request '{request.Method}' failed: {ex}");
                if (request.HasId)
                    await WriteErrorAsync(output, request.Id, -32603, "Internal error.", null, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleNotificationAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        // MCP currently uses notifications/initialized. Unknown notifications are intentionally ignored.
        if (request.Method.Equals("notifications/cancelled", StringComparison.Ordinal))
            await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<object> DispatchAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case "initialize":
                return new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "openmeow-lab", version = "1.0" },
                };
            case "tools/list":
                return new { tools = ToolDefinitions };
            case "tools/call":
                return await CallToolAsync(request.Params, cancellationToken).ConfigureAwait(false);
            default:
                throw new RpcException(-32601, $"Method '{request.Method}' not found.");
        }
    }

    private async Task<object> CallToolAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("name", out JsonElement nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
            throw new RpcException(-32602, "tools/call requires a tool name.");

        string name = nameElement.GetString()!;
        if (!ToolDefinitions.Any(tool => tool.Name.Equals(name, StringComparison.Ordinal)))
            throw new RpcException(-32601, $"Tool '{name}' not found.");

        JsonElement arguments = parameters.TryGetProperty("arguments", out JsonElement supplied)
            ? supplied.Clone()
            : EmptyArguments;
        if (arguments.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            throw new RpcException(-32602, "Tool arguments must be a JSON object.");

        try
        {
            object value = await ExecuteToolAsync(name, arguments.ValueKind == JsonValueKind.Null ? EmptyArguments : arguments, cancellationToken)
                .ConfigureAwait(false);
            return ToolResult(value, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"tool '{name}' failed: {ex.Message}");
            return ToolResult(new ErrorPayload(ErrorCode(ex), ex.Message, ErrorDetails(ex)), true);
        }
    }

    private async Task<object> ExecuteToolAsync(string name, JsonElement args, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case "list_subjects": return _tower.Subjects.All;
            case "list_tasks": return _tower.Tasks;
            case "create_experiment": return _tower.Create(Deserialize<CreateExperimentRequest>(args));
            case "observe": return _tower.Observe(ReadExperimentId(args));
            case "act": return _tower.Act(ReadExperimentId(args), DeserializeAction(args));
            case "run_sequence": return _tower.RunSequence(ReadExperimentId(args), DeserializeSequence(args));
            case "evaluate": return _tower.Evaluate(ReadExperimentId(args));
            case "compare": return _tower.Compare(ReadExperimentIds(args));
            case "auto_tune": return await _tower.AutoTuneAsync(Deserialize<TuneRequest>(args), cancellationToken).ConfigureAwait(false);
            case "run_gait_benchmark": return _tower.RunGaitBenchmark(Deserialize<GaitBenchmarkRequest>(args), cancellationToken);
            case "auto_tune_gait": return await _tower.AutoTuneGaitAsync(Deserialize<GaitAutotuneRequest>(args), cancellationToken).ConfigureAwait(false);
            case "preview_gait_driver_profile":
                {
                    GaitDriverProfileArguments gaitProfile = DeserializeGaitDriverProfile(args, requireEnableBodyTrackers: false);
                    return _tower.PreviewGaitDriverProfile(gaitProfile.Profile, gaitProfile.BasePreset);
                }
            case "apply_gait_driver_profile":
                {
                    GaitDriverProfileArguments gaitProfile = DeserializeGaitDriverProfile(args, requireEnableBodyTrackers: true);
                    return _tower.ApplyGaitDriverProfile(gaitProfile.Profile, gaitProfile.BasePreset, gaitProfile.EnableBodyTrackers!.Value);
                }
            case "recommend_bindings":
                {
                    BindingArguments binding = Deserialize<BindingArguments>(args);
                    return _tower.RecommendBindings(binding.Layout, binding.Baseline);
                }
            case "preview_driver_profile":
                {
                    DriverProfileArguments driverProfile = Deserialize<DriverProfileArguments>(args);
                    return _tower.PreviewDriverProfile(driverProfile.Profile, driverProfile.BasePreset);
                }
            case "apply_driver_profile":
                {
                    DriverProfileArguments driverProfile = Deserialize<DriverProfileArguments>(args);
                    return _tower.ApplyDriverProfile(driverProfile.Profile, driverProfile.BasePreset);
                }
            case "set_profile":
                {
                    Guid id = ReadExperimentId(args);
                    if (!args.TryGetProperty("profile", out JsonElement profileElement))
                        throw new RpcException(-32602, "A profile is required.");
                    MotionProfile profile = Deserialize<MotionProfile>(profileElement);
                    return _tower.SetProfile(id, profile, ReadNullableInt64(args, "expectedRevision"));
                }
            case "reset":
                {
                    Guid id = ReadExperimentId(args);
                    long? expected = ReadNullableInt64(args, "expectedRevision");
                    return _tower.Reset(id, expected);
                }
            case "delete_experiment":
                {
                    Guid id = ReadExperimentId(args);
                    if (!_tower.Remove(id))
                        throw new KeyNotFoundException($"Unknown experiment '{id}'.");
                    return new { removed = true, experimentId = id };
                }
            default: throw new RpcException(-32601, $"Tool '{name}' not found.");
        }
    }

    private static object ToolResult(object value, bool isError) => new
    {
        content = new[]
        {
            new { type = "text", text = JsonTransport.Serialize(value) },
        },
        isError,
        structuredContent = value,
    };

    private static readonly object Vec3Schema = Schema(
        new Dictionary<string, object>
        {
            ["x"] = Property("number", "World X coordinate in metres."),
            ["y"] = Property("number", "World Y coordinate in metres."),
            ["z"] = Property("number", "World Z coordinate in metres."),
        },
        ["x", "y", "z"]);

    private static readonly object MotionProfileSchema = Schema(
        new Dictionary<string, object>
        {
            ["name"] = Property("string", "Human-readable candidate name."),
            ["positionSpringHz"] = Property("number", "Hand tracking spring frequency, 1-20 Hz."),
            ["dampingRatio"] = Property("number", "Hand damping ratio, 0.2-2."),
            ["maxSpeed"] = Property("number", "Maximum hand speed in metres/second."),
            ["maxAcceleration"] = Property("number", "Maximum hand acceleration in metres/second squared."),
            ["contactCompliance"] = Property("number", "Contact yielding from 0 (rigid) to 1 (soft)."),
            ["predictionSeconds"] = Property("number", "Target prediction lead, 0-0.2 seconds."),
            ["handRadius"] = Property("number", "Collision radius of the virtual hand in metres."),
            ["bindings"] = new { type = "object", additionalProperties = new { type = "string" } },
        });

    private static readonly object ActionSchema = Schema(
        new Dictionary<string, object>
        {
            ["expectedRevision"] = Property("integer", "Optional optimistic-concurrency revision."),
            ["hand"] = new { type = "string", @enum = new[] { "left", "right" } },
            ["target"] = Vec3Schema,
            ["durationSeconds"] = new { type = "number", minimum = TickSecondsForSchema, maximum = 10 },
            ["grip"] = Property("boolean", "Whether the hand is gripping during this motion."),
            ["measureSettling"] = Property(
                "boolean",
                "On a non-gripping retreat, start a fresh 0.9 second post-contact settling window after it leaves the task target."),
            ["label"] = Property("string", "Agent-provided action label for trace readability."),
        },
        ["target"]);

    private static readonly object SequenceSchema = Schema(
        new Dictionary<string, object>
        {
            ["expectedRevision"] = Property("integer", "Optional optimistic-concurrency revision."),
            ["actions"] = new { type = "array", items = ActionSchema, minItems = 1, maxItems = 128 },
        },
        ["actions"]);

    private const double TickSecondsForSchema = 1.0 / 90.0;

    private static readonly ToolDefinition[] ToolDefinitions =
    [
        new("list_subjects", "List available research subjects.", Schema()),
        new("list_tasks", "List available research tasks.", Schema()),
        new("create_experiment", "Create an isolated experiment world.", Schema(
            new Dictionary<string, object>
            {
                ["subjectId"] = Property("string", "Subject id from list_subjects; defaults to default_mew."),
                ["taskId"] = Property("string", "Research task id from list_tasks; defaults to head_petting."),
                ["agentId"] = Property("string", "Stable label for the controlling agent."),
                ["seed"] = Property("integer", "Deterministic experiment seed."),
                ["profile"] = MotionProfileSchema,
            })),
        new("observe", "Read an experiment snapshot.", ExperimentSchema()),
        new("act", "Advance one hand action in an experiment.", Schema(
            new Dictionary<string, object>
            {
                ["experimentId"] = ExperimentIdProperty(),
                ["action"] = ActionSchema,
            },
            ["experimentId", "action"])),
        new("run_sequence", "Atomically advance a sequence of hand actions.", Schema(
            new Dictionary<string, object>
            {
                ["experimentId"] = ExperimentIdProperty(),
                ["sequence"] = SequenceSchema,
            },
            ["experimentId", "sequence"])),
        new("evaluate", "Evaluate an experiment.", ExperimentSchema()),
        new("compare", "Compare two or more experiment evaluations.", Schema(
            new Dictionary<string, object>
            {
                ["experimentIds"] = new
                {
                    type = "array",
                    items = new { type = "string", format = "uuid" },
                    minItems = 2,
                },
            },
            ["experimentIds"])),
        new("auto_tune", "Search motion profiles in parallel and return a deterministic winner.", Schema(
            new Dictionary<string, object>
            {
                ["taskId"] = Property("string", "Research task to benchmark."),
                ["subjectId"] = Property("string", "Research subject to benchmark."),
                ["seed"] = Property("integer", "Deterministic candidate-generation seed."),
                ["candidates"] = new { type = "integer", minimum = 2, maximum = 128 },
                ["parallelism"] = new { type = "integer", minimum = 1, maximum = 12 },
                ["baseline"] = MotionProfileSchema,
            })),
        new("run_gait_benchmark", "Run the deterministic 90 Hz full-body gait benchmark.", Schema(
            new Dictionary<string, object>
            {
                ["seed"] = Property("integer", "Deterministic simulator seed."),
                ["profile"] = GaitProfileSchema,
                ["scenario"] = GaitScenarioSchema,
            })),
        new("auto_tune_gait", "Search full-body gait profiles in parallel with a deterministic winner.", Schema(
            new Dictionary<string, object>
            {
                ["seed"] = Property("integer", "Deterministic candidate-generation seed."),
                ["candidates"] = new { type = "integer", minimum = 2, maximum = 128 },
                ["parallelism"] = new { type = "integer", minimum = 1, maximum = 16 },
                ["baseline"] = GaitProfileSchema,
            })),
        new("preview_gait_driver_profile", "Preview all nine gait fields mapped to desktop settings without writing a file.", GaitDriverProfileSchema(false)),
        new("apply_gait_driver_profile", "Explicitly save all nine gait fields and tracker topology to desktop settings.", GaitDriverProfileSchema(true)),
        new("recommend_bindings", "Recommend a keyboard/mouse binding layout.", Schema(
            new Dictionary<string, object>
            {
                ["layout"] = new
                {
                    type = "string",
                    @enum = new[] { "default", "right_handed", "left_handed", "compact" },
                },
                ["baseline"] = MotionProfileSchema,
            },
            ["layout"])),
        new("preview_driver_profile", "Preview a research profile mapped to the desktop driver's second-order hand settings without writing any file.", DriverProfileSchema()),
        new("apply_driver_profile", "Explicitly write a research profile mapping to the desktop driver's fixed LocalAppData settings file. This is never automatic.", DriverProfileSchema()),
        new("reset", "Reset an experiment to its initial state.", RevisionedExperimentSchema()),
        new("set_profile", "Replace an experiment motion profile.", Schema(
            new Dictionary<string, object>
            {
                ["experimentId"] = ExperimentIdProperty(),
                ["profile"] = MotionProfileSchema,
                ["expectedRevision"] = Property("integer", "Optional optimistic-concurrency revision."),
            },
            ["experimentId", "profile"])),
        new("delete_experiment", "Delete an experiment and release its state.", ExperimentSchema()),
    ];

    private static object Schema(IReadOnlyDictionary<string, object>? properties = null, string[]? required = null) =>
        new
        {
            type = "object",
            properties = properties ?? new Dictionary<string, object>(),
            required = required ?? [],
            additionalProperties = false,
        };

    private static object Property(string type, string description) => new { type, description };
    private static object ExperimentIdProperty() =>
        new { type = "string", format = "uuid", description = "Experiment id returned by create_experiment." };
    private static object ExperimentSchema() => Schema(
        new Dictionary<string, object> { ["experimentId"] = ExperimentIdProperty() },
        ["experimentId"]);
    private static object RevisionedExperimentSchema() => Schema(
        new Dictionary<string, object>
        {
            ["experimentId"] = ExperimentIdProperty(),
            ["expectedRevision"] = Property("integer", "Optional optimistic-concurrency revision."),
        },
            ["experimentId"]);
    private static object DriverProfileSchema() => Schema(
        new Dictionary<string, object>
        {
            ["profile"] = MotionProfileSchema,
            ["basePreset"] = new
            {
                type = "string",
                @enum = DesktopMotionSettings.PresetNames,
                description = "Existing desktop preset whose non-hand settings are preserved; defaults to Natural.",
            },
        },
        ["profile"]);

    private static object GaitProfileSchema => Schema(
        new Dictionary<string, object>
        {
            ["bodyHeightMeters"] = Property("number", "Whole-body height in metres."),
            ["hipFollowTau"] = Property("number", "Hip velocity follow time constant in seconds."),
            ["hipLeanDegrees"] = Property("number", "Forward lean angle at walking speed."),
            ["footSpacingMeters"] = Property("number", "Distance between planted feet."),
            ["strideLengthMeters"] = Property("number", "World distance covered by one step."),
            ["stepHeightMeters"] = Property("number", "Peak swing-foot clearance."),
            ["gaitSmoothingTau"] = Property("number", "Command and turn smoothing time constant."),
            ["turnToeDegrees"] = Property("number", "Toe yaw offset while turning in place."),
            ["footPlantStrength"] = Property("number", "Blend from free foot motion to world locking, 0-1."),
        });

    private static object GaitScenarioSchema => new
    {
        type = "array",
        maxItems = 128,
        items = Schema(new Dictionary<string, object>
        {
            ["command"] = new { type = "string", @enum = new[] { "idle", "forward", "strafe", "turnInPlace", "diagonal", "stop" } },
            ["durationSeconds"] = new { type = "number", minimum = 0, maximum = 30 },
            ["speedMultiplier"] = new { type = "number", minimum = .1, maximum = 3 },
        }, ["command", "durationSeconds"]),
    };

    private static object GaitDriverProfileSchema(bool requireEnableBodyTrackers) => Schema(
        new Dictionary<string, object>
        {
            ["profile"] = GaitProfileSchema,
            ["basePreset"] = new
            {
                type = "string",
                @enum = DesktopMotionSettings.PresetNames,
                description = "Existing desktop preset whose non-gait settings are preserved; defaults to Natural.",
            },
            ["enableBodyTrackers"] = Property("boolean", "Explicitly enable or disable GenericTracker body topology. Required for apply; ignored by preview."),
        },
        requireEnableBodyTrackers ? ["profile", "enableBodyTrackers"] : ["profile"]);

    private static T Deserialize<T>(JsonElement args) =>
        args.Deserialize<T>(JsonTransport.Options) ?? throw new RpcException(-32602, "Tool arguments must not be null.");

    private static GaitDriverProfileArguments DeserializeGaitDriverProfile(JsonElement args, bool requireEnableBodyTrackers)
    {
        JsonElement profileElement = args.TryGetProperty("profile", out JsonElement nested) ? nested : args;
        GaitProfile profile = Deserialize<GaitProfile>(profileElement);
        string basePreset = "Natural";
        if (args.TryGetProperty("basePreset", out JsonElement presetElement))
        {
            if (presetElement.ValueKind != JsonValueKind.String)
                throw new RpcException(-32602, "basePreset must be a string.");
            basePreset = presetElement.GetString() ?? "Natural";
        }
        bool? enable = null;
        if (args.TryGetProperty("enableBodyTrackers", out JsonElement enableElement))
        {
            if (enableElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new RpcException(-32602, "enableBodyTrackers must be a boolean.");
            enable = enableElement.GetBoolean();
        }
        if (requireEnableBodyTrackers && !enable.HasValue)
            throw new RpcException(-32602, "apply requires an explicit enableBodyTrackers boolean.");
        return new GaitDriverProfileArguments(profile, basePreset, enable);
    }

    private static ActionRequest DeserializeAction(JsonElement args)
    {
        JsonElement value = args.TryGetProperty("action", out JsonElement nested) ? nested : args;
        return Deserialize<ActionRequest>(value);
    }

    private static SequenceRequest DeserializeSequence(JsonElement args)
    {
        JsonElement value = args.TryGetProperty("sequence", out JsonElement nested) ? nested : args;
        return Deserialize<SequenceRequest>(value);
    }

    private static Guid ReadExperimentId(JsonElement args)
    {
        if (!args.TryGetProperty("experimentId", out JsonElement value) || value.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(value.GetString(), out Guid id))
            throw new RpcException(-32602, "A valid experimentId is required.");
        return id;
    }

    private static Guid[] ReadExperimentIds(JsonElement args)
    {
        JsonElement values = args;
        if (args.ValueKind == JsonValueKind.Object &&
            !args.TryGetProperty("experimentIds", out values) && !args.TryGetProperty("ids", out values))
            throw new RpcException(-32602, "An experimentIds array is required.");
        if (values.ValueKind != JsonValueKind.Array)
            throw new RpcException(-32602, "experimentIds must be an array.");
        return values.EnumerateArray().Select(value =>
            value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out Guid id)
                ? id
                : throw new RpcException(-32602, "Every experiment id must be a GUID.")).ToArray();
    }

    private static long? ReadNullableInt64(JsonElement args, string propertyName) =>
        !args.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind == JsonValueKind.Null
            ? null
            : value.TryGetInt64(out long result)
                ? result
                : throw new RpcException(-32602, $"'{propertyName}' must be an integer.");

    private static JsonRpcRequest ParseRequest(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new RpcException(-32600, "Invalid Request.");
        if (!root.TryGetProperty("jsonrpc", out JsonElement version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
            throw new RpcException(-32600, "jsonrpc must be '2.0'.");
        if (!root.TryGetProperty("method", out JsonElement method) || method.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(method.GetString()))
            throw new RpcException(-32600, "A method is required.");

        bool hasId = root.TryGetProperty("id", out JsonElement id);
        JsonElement parameters = root.TryGetProperty("params", out JsonElement supplied) ? supplied.Clone() : EmptyArguments;
        return new JsonRpcRequest(method.GetString()!, hasId, hasId ? id.Clone() : JsonTransport.NullId, parameters);
    }

    private static bool requestHasId(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("id", out _);
        }
        catch { return false; }
    }

    private static async Task WriteResultAsync(TextWriter output, JsonElement id, object result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await output.WriteLineAsync(JsonTransport.Serialize(new { jsonrpc = "2.0", id, result })).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(TextWriter output, JsonElement id, int code, string message, object? data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await output.WriteLineAsync(JsonTransport.Serialize(new { jsonrpc = "2.0", id, error = new { code, message, data } })).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    private void Log(string message)
    {
        try { _diagnostics.WriteLine($"[mcp] {message}"); _diagnostics.Flush(); } catch { }
    }

    private static string ErrorCode(Exception exception) => exception switch
    {
        StaleRevisionException => "stale_revision",
        KeyNotFoundException => "not_found",
        ArgumentException or JsonException or InvalidDataException or FormatException => "invalid_request",
        _ => "internal_error",
    };

    private static object? ErrorDetails(Exception exception) => exception is StaleRevisionException stale
        ? new { expectedRevision = stale.Expected, actualRevision = stale.Actual }
        : null;

    private sealed record JsonRpcRequest(string Method, bool HasId, JsonElement Id, JsonElement Params);
    private sealed record ToolDefinition(string Name, string Description, object InputSchema);
    private sealed record BindingArguments(string Layout = "default", MotionProfile? Baseline = null);
    private sealed record DriverProfileArguments(MotionProfile Profile, string BasePreset = "Natural");
    private sealed record GaitDriverProfileArguments(GaitProfile Profile, string BasePreset, bool? EnableBodyTrackers);
    private sealed record ErrorPayload(string Code, string Message, object? Details);
    private sealed class RpcException(int code, string message, object? data = null) : InvalidOperationException(message)
    {
        public int Code { get; } = code;
        public object? Payload { get; } = data;
    }
}
