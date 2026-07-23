using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using OpenMeow.Lab.Domain;
using OpenMeow.Lab.Orchestration;
using OpenMeow.Lab.Simulation;

namespace OpenMeow.Lab;

/// <summary>
/// A small loopback-only JSON API for driving the research control tower.
/// </summary>
public sealed class LocalhostHttpServer : IAsyncDisposable, IDisposable
{
    public const int DefaultMaxBodyBytes = 1 * 1024 * 1024;

    private readonly ControlTower _tower;
    private readonly HttpListener _listener = new();
    private readonly int _maxBodyBytes;
    private readonly ConcurrentDictionary<long, Task> _inflight = new();
    private readonly object _lifecycleGate = new();
    private CancellationTokenRegistration _stopRegistration;
    private CancellationTokenSource? _runCancellation;
    private Task? _acceptLoop;
    private int _started;
    private int _disposed;
    private bool _asyncStopInProgress;
    private long _requestId;

    public LocalhostHttpServer(ControlTower tower, int port, int maxBodyBytes = DefaultMaxBodyBytes)
    {
        _tower = tower ?? throw new ArgumentNullException(nameof(tower));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
        if (maxBodyBytes is < 1024 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maxBodyBytes), "Request body limit must be 1 KiB-16 MiB.");

        Port = port;
        _maxBodyBytes = maxBodyBytes;
        BaseAddress = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
        // Never accept an externally reachable prefix. HttpListener matches this exact loopback prefix.
        _listener.Prefixes.Add(BaseAddress.AbsoluteUri);
        _listener.IgnoreWriteExceptions = true;
    }

    public int Port { get; }
    public Uri BaseAddress { get; }
    public bool IsRunning => Volatile.Read(ref _started) != 0 && IsListenerRunning();

    private bool IsListenerRunning()
    {
        try { return _listener.IsListening; }
        catch (ObjectDisposedException) { return false; }
    }

    public void Start(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        lock (_lifecycleGate)
        {
            ThrowIfDisposed();
            if (Volatile.Read(ref _started) != 0)
                return;
            if (_asyncStopInProgress)
                throw new InvalidOperationException("The HTTP server is still stopping.");
            if (_acceptLoop is { IsCompleted: false })
                throw new InvalidOperationException(
                    "The previous HTTP run is still stopping; await StopAsync before restarting.");
            if (_acceptLoop is { IsCompleted: true })
            {
                _acceptLoop = null;
                _runCancellation?.Dispose();
                _runCancellation = null;
            }

            try
            {
                _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _listener.Start();
                Volatile.Write(ref _started, 1);
                if (cancellationToken.CanBeCanceled)
                    _stopRegistration = cancellationToken.Register(static state => ((LocalhostHttpServer)state!).Stop(), this);
                _acceptLoop = AcceptLoopAsync(_runCancellation.Token);
            }
            catch
            {
                Volatile.Write(ref _started, 0);
                try { _listener.Stop(); }
                catch (ObjectDisposedException) { }
                _runCancellation?.Dispose();
                _runCancellation = null;
                throw;
            }
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Start(cancellationToken);
        Task loop = _acceptLoop ?? Task.CompletedTask;
        await loop.ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_lifecycleGate)
        {
            if (_asyncStopInProgress)
                throw new InvalidOperationException("The HTTP server is already stopping.");
            _asyncStopInProgress = true;
            StopUnderLock();
            loop = _acceptLoop;
        }
        try
        {
            if (loop is not null)
                await loop.ConfigureAwait(false);
            await WaitForInflightAsync().ConfigureAwait(false);
            DisposeRunCancellation();
        }
        finally
        {
            lock (_lifecycleGate)
                _asyncStopInProgress = false;
        }
    }

    public void Stop()
    {
        lock (_lifecycleGate)
            StopUnderLock();
    }

    private void StopUnderLock()
    {
        if (Volatile.Read(ref _started) == 0)
            return;
        Volatile.Write(ref _started, 0);
        try { _runCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        _stopRegistration.Dispose();
        _stopRegistration = default;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Stop();
        try { _listener.Close(); } catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Stop();
        Task? loop = _acceptLoop;
        if (loop is not null)
            await loop.ConfigureAwait(false);
        await WaitForInflightAsync().ConfigureAwait(false);
        DisposeRunCancellation();
        try { _listener.Close(); } catch (ObjectDisposedException) { }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (IsRunning && !cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (HttpListenerException) when (!IsRunning || cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }

            long id = Interlocked.Increment(ref _requestId);
            Task request = HandleAsync(context, cancellationToken);
            _inflight[id] = request;
            _ = request.ContinueWith(
                (_completed, state) =>
                {
                    if (state is ConcurrentDictionary<long, Task> pending)
                    {
                        pending.TryRemove(id, out Task? removed);
                        _ = removed;
                    }
                },
                _inflight,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken serverCancellation)
    {
        try
        {
            await DispatchAsync(context, serverCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (serverCancellation.IsCancellationRequested || !IsRunning)
        {
            // The listener is stopping; there is no useful response to send.
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(context.Response, ErrorStatus(ex), ErrorCode(ex), ex.Message, ErrorDetails(ex), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            try { context.Response.Close(); }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task DispatchAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        HttpListenerRequest request = context.Request;
        string method = request.HttpMethod.ToUpperInvariant();
        string[] segments = request.Url?.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];

        if (segments.Any(segment => segment.Length > 128))
            throw new ArgumentException("URL path segment is too long.");

        if (method == "GET" && segments is ["health"])
        {
            await WriteJsonAsync(context.Response, new { status = "ok" }, 200, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && segments is ["subjects"])
        {
            await WriteJsonAsync(context.Response, _tower.Subjects.All, 200, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && segments is ["tasks"])
        {
            await WriteJsonAsync(context.Response, _tower.Tasks, 200, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && segments is ["experiments"])
        {
            await WriteJsonAsync(context.Response, _tower.List(), 200, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && segments is ["experiments"])
        {
            CreateExperimentRequest body = await ReadJsonAsync<CreateExperimentRequest>(request, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(context.Response, _tower.Create(body), 201, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && segments is ["compare"])
        {
            Guid[] ids = await ReadGuidListAsync(request, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(context.Response, _tower.Compare(ids), 200, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && segments is ["autotune"])
        {
            TuneRequest body = await ReadJsonAsync<TuneRequest>(request, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(context.Response, await _tower.AutoTuneAsync(body, cancellationToken).ConfigureAwait(false), 200, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (method == "POST" && segments is ["gait", "benchmark"])
        {
            GaitBenchmarkRequest body = await ReadJsonAsync<GaitBenchmarkRequest>(request, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(context.Response, _tower.RunGaitBenchmark(body, cancellationToken), 200, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && segments is ["gait", "autotune"])
        {
            GaitAutotuneRequest body = await ReadJsonAsync<GaitAutotuneRequest>(request, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(context.Response, await _tower.AutoTuneGaitAsync(body, cancellationToken).ConfigureAwait(false), 200, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (method == "POST" && segments is ["bindings"])
        {
            BindingRequest body = await ReadJsonAsync<BindingRequest>(request, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(context.Response, _tower.RecommendBindings(body.Layout, body.Baseline), 200, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && segments is ["driver-profile", "preview"])
        {
            DriverProfileRequest body = await ReadDriverProfileRequestAsync(request, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(
                    context.Response,
                    _tower.PreviewDriverProfile(body.Profile, body.BasePreset),
                    200,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (method == "POST" && segments is ["driver-profile", "apply"])
        {
            DriverProfileRequest body = await ReadDriverProfileRequestAsync(request, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(
                    context.Response,
                    _tower.ApplyDriverProfile(body.Profile, body.BasePreset),
                    200,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (method == "POST" && segments is ["gait", "driver-profile", "preview"])
        {
            GaitDriverProfileRequest body = await ReadGaitDriverProfileRequestAsync(request, cancellationToken, requireEnableBodyTrackers: false)
                .ConfigureAwait(false);
            await WriteJsonAsync(
                    context.Response,
                    _tower.PreviewGaitDriverProfile(body.Profile, body.BasePreset),
                    200,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (method == "POST" && segments is ["gait", "driver-profile", "apply"])
        {
            GaitDriverProfileRequest body = await ReadGaitDriverProfileRequestAsync(request, cancellationToken, requireEnableBodyTrackers: true)
                .ConfigureAwait(false);
            await WriteJsonAsync(
                    context.Response,
                    _tower.ApplyGaitDriverProfile(body.Profile, body.BasePreset, body.EnableBodyTrackers!.Value),
                    200,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (segments.Length >= 2 && segments[0].Equals("experiments", StringComparison.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(segments[1], out Guid id))
                throw new FormatException("Experiment id must be a GUID.");

            if (method == "GET" && segments.Length == 2)
            {
                await WriteJsonAsync(context.Response, _tower.Observe(id), 200, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (method == "DELETE" && segments.Length == 2)
            {
                if (!_tower.Remove(id)) throw new KeyNotFoundException($"Unknown experiment '{id}'.");
                await WriteJsonAsync(context.Response, new { removed = true, experimentId = id }, 200, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (method == "POST" && segments.Length == 3)
            {
                switch (segments[2].ToLowerInvariant())
                {
                    case "act":
                        {
                            ActionRequest body = await ReadJsonAsync<ActionRequest>(request, cancellationToken).ConfigureAwait(false);
                            await WriteJsonAsync(context.Response, _tower.Act(id, body), 200, cancellationToken).ConfigureAwait(false);
                            return;
                        }
                    case "sequence":
                        {
                            SequenceRequest body = await ReadJsonAsync<SequenceRequest>(request, cancellationToken).ConfigureAwait(false);
                            await WriteJsonAsync(context.Response, _tower.RunSequence(id, body), 200, cancellationToken).ConfigureAwait(false);
                            return;
                        }
                    case "evaluate":
                        await WriteJsonAsync(context.Response, _tower.Evaluate(id), 200, cancellationToken).ConfigureAwait(false);
                        return;
                    case "reset":
                        {
                            string resetJson = await ReadBodyAsync(request, cancellationToken, allowEmpty: true).ConfigureAwait(false);
                            ResetRequest body = string.IsNullOrWhiteSpace(resetJson)
                                ? new ResetRequest(null)
                                : JsonTransport.Deserialize<ResetRequest>(resetJson);
                            await WriteJsonAsync(context.Response, _tower.Reset(id, body.ExpectedRevision), 200, cancellationToken).ConfigureAwait(false);
                            return;
                        }
                    case "profile":
                        {
                            ProfileRequest body = await ReadProfileRequestAsync(request, cancellationToken).ConfigureAwait(false);
                            await WriteJsonAsync(context.Response, _tower.SetProfile(id, body.Profile, body.ExpectedRevision), 200, cancellationToken)
                                .ConfigureAwait(false);
                            return;
                        }
                }
            }
        }

        throw new HttpRouteException(404, "not_found", "The requested endpoint does not exist.");
    }

    private async Task<ProfileRequest> ReadProfileRequestAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        string body = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Profile request must be a JSON object.");
        if (root.TryGetProperty("profile", out JsonElement profileElement))
        {
            MotionProfile profile = profileElement.Deserialize<MotionProfile>(JsonTransport.Options)
                ?? throw new JsonException("Profile must not be null.");
            long? expected = ReadOptionalInt64(root, "expectedRevision");
            return new ProfileRequest(profile, expected);
        }

        MotionProfile direct = root.Deserialize<MotionProfile>(JsonTransport.Options)
            ?? throw new JsonException("Profile must not be null.");
        long? directExpected = ReadOptionalInt64(root, "expectedRevision");
        return new ProfileRequest(direct, directExpected);
    }

    private async Task<DriverProfileRequest> ReadDriverProfileRequestAsync(
        HttpListenerRequest request,
        CancellationToken cancellationToken)
    {
        string body = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Driver profile request must be a JSON object.");

        string basePreset = "Natural";
        if (root.TryGetProperty("basePreset", out JsonElement presetElement))
        {
            if (presetElement.ValueKind != JsonValueKind.String)
                throw new JsonException("basePreset must be a string.");
            basePreset = presetElement.GetString() ?? "Natural";
        }
        if (root.TryGetProperty("profile", out JsonElement profileElement))
        {
            MotionProfile profile = profileElement.Deserialize<MotionProfile>(JsonTransport.Options)
                ?? throw new JsonException("Profile must not be null.");
            return new DriverProfileRequest { Profile = profile, BasePreset = basePreset };
        }

        // Also accept a direct MotionProfile body for parity with /profile.
        MotionProfile direct = root.Deserialize<MotionProfile>(JsonTransport.Options)
            ?? throw new JsonException("Profile must not be null.");
        return new DriverProfileRequest { Profile = direct, BasePreset = basePreset };
    }

    private async Task<GaitDriverProfileRequest> ReadGaitDriverProfileRequestAsync(
        HttpListenerRequest request,
        CancellationToken cancellationToken,
        bool requireEnableBodyTrackers)
    {
        string body = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Gait driver profile request must be a JSON object.");

        string basePreset = "Natural";
        if (root.TryGetProperty("basePreset", out JsonElement presetElement))
        {
            if (presetElement.ValueKind != JsonValueKind.String)
                throw new JsonException("basePreset must be a string.");
            basePreset = presetElement.GetString() ?? "Natural";
        }

        bool? enable = null;
        if (root.TryGetProperty("enableBodyTrackers", out JsonElement enableElement))
        {
            if (enableElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new JsonException("enableBodyTrackers must be a boolean.");
            enable = enableElement.GetBoolean();
        }
        if (requireEnableBodyTrackers && !enable.HasValue)
            throw new JsonException("apply requires an explicit enableBodyTrackers boolean.");

        JsonElement profileElement = root.TryGetProperty("profile", out JsonElement nested)
            ? nested
            : root;
        GaitProfile profile = profileElement.Deserialize<GaitProfile>(JsonTransport.Options)
            ?? throw new JsonException("Gait profile must not be null.");
        return new GaitDriverProfileRequest(profile, basePreset, enable);
    }

    private async Task<Guid[]> ReadGuidListAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        string body = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        JsonElement values = root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (!root.TryGetProperty("experimentIds", out values) && !root.TryGetProperty("ids", out values))
                throw new JsonException("Expected an 'experimentIds' or 'ids' array.");
        }
        if (values.ValueKind != JsonValueKind.Array)
            throw new JsonException("Experiment ids must be an array.");
        Guid[] ids = values.EnumerateArray().Select(item =>
            item.ValueKind == JsonValueKind.String &&
            Guid.TryParse(item.GetString(), out Guid id)
                ? id
                : throw new JsonException("Each experiment id must be a GUID.")).ToArray();
        return ids;
    }

    private static long? ReadOptionalInt64(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long result))
            throw new JsonException($"{propertyName} must be a 64-bit integer.");
        return result;
    }

    private async Task<T> ReadJsonAsync<T>(HttpListenerRequest request, CancellationToken cancellationToken, bool allowEmpty = false)
    {
        string body = await ReadBodyAsync(request, cancellationToken, allowEmpty).ConfigureAwait(false);
        return allowEmpty && string.IsNullOrWhiteSpace(body) ? Activator.CreateInstance<T>() : JsonTransport.Deserialize<T>(body);
    }

    private async Task<string> ReadBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken, bool allowEmpty = false)
    {
        if (request.ContentLength64 > _maxBodyBytes)
            throw new BodyTooLargeException(_maxBodyBytes);
        if (request.ContentLength64 == 0)
            return allowEmpty ? string.Empty : throw new InvalidDataException("A JSON request body is required.");

        await using Stream input = request.InputStream;
        using var output = new MemoryStream(Math.Min(_maxBodyBytes, (int)Math.Max(request.ContentLength64, 0)));
        byte[] buffer = new byte[81920];
        int total = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > _maxBodyBytes) throw new BodyTooLargeException(_maxBodyBytes);
            output.Write(buffer, 0, read);
        }
        if (total == 0 && !allowEmpty)
            throw new InvalidDataException("A JSON request body is required.");
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, object value, int statusCode, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonTransport.Serialize(value));
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(
        HttpListenerResponse response,
        int statusCode,
        string code,
        string message,
        object? details,
        CancellationToken cancellationToken)
    {
        var error = new { error = new { code, message, details } };
        try { await WriteJsonAsync(response, error, statusCode, cancellationToken).ConfigureAwait(false); }
        catch (HttpListenerException) { }
        catch (ObjectDisposedException) { }
    }

    private static int ErrorStatus(Exception exception) => exception switch
    {
        HttpRouteException route => route.StatusCode,
        BodyTooLargeException => 413,
        StaleRevisionException => 409,
        KeyNotFoundException => 404,
        FormatException or JsonException or InvalidDataException or ArgumentException or OverflowException => 400,
        _ => 500,
    };

    private static string ErrorCode(Exception exception) => exception switch
    {
        HttpRouteException route => route.Code,
        BodyTooLargeException => "body_too_large",
        StaleRevisionException => "stale_revision",
        KeyNotFoundException => "not_found",
        FormatException or JsonException or InvalidDataException or ArgumentException or OverflowException => "invalid_request",
        _ => "internal_error",
    };

    private static object? ErrorDetails(Exception exception) => exception switch
    {
        StaleRevisionException stale => new { expectedRevision = stale.Expected, actualRevision = stale.Actual },
        BodyTooLargeException tooLarge => new { maximumBytes = tooLarge.MaximumBytes },
        _ => null,
    };

    private async Task WaitForInflightAsync()
    {
        while (!_inflight.IsEmpty)
            await Task.WhenAll(_inflight.Values.ToArray()).ConfigureAwait(false);
    }

    private void DisposeRunCancellation()
    {
        lock (_lifecycleGate)
        {
            _runCancellation?.Dispose();
            _runCancellation = null;
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(LocalhostHttpServer));
    }

    private sealed record ProfileRequest(MotionProfile Profile, long? ExpectedRevision);
    private sealed record ResetRequest(long? ExpectedRevision);
    public sealed record DriverProfileRequest
    {
        public MotionProfile Profile { get; init; } = new();
        public string BasePreset { get; init; } = "Natural";
    }

    private sealed record GaitDriverProfileRequest(
        GaitProfile Profile,
        string BasePreset,
        bool? EnableBodyTrackers);
    public sealed record BindingRequest(string Layout = "default", MotionProfile? Baseline = null);
    private sealed class BodyTooLargeException(int maximumBytes)
        : InvalidOperationException($"Request body exceeds the {maximumBytes} byte limit.")
    {
        public int MaximumBytes { get; } = maximumBytes;
    }

    private sealed class HttpRouteException(int statusCode, string code, string message) : InvalidOperationException(message)
    {
        public int StatusCode { get; } = statusCode;
        public string Code { get; } = code;
    }
}
