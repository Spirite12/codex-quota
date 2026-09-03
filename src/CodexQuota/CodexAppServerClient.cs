using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexQuota;

internal sealed class CodexAppServerClient : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _disposeSync = new();
    private Process? _process;
    private StreamWriter? _input;
    private Task? _disposeTask;
    private int _nextRequestId;

    public event EventHandler? RateLimitsUpdated;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("App Server has already been started.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = CodexHost.ResolveCodexExecutablePath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动本机 Codex App Server。");
        _input = _process.StandardInput;
        _input.AutoFlush = true;

        _ = PumpOutputAsync(_shutdown.Token);
        _ = DrainErrorAsync(_shutdown.Token);

        var initialize = RequestAsync(
            "initialize",
            new
            {
                clientInfo = new
                {
                    name = "codex-quota",
                    title = "Codex Quota",
                    version = "0.1.0"
                }
            },
            cancellationToken);

        await SendNotificationAsync("initialized", new { }, cancellationToken);
        await initialize;
    }

    public async Task<AccountState> GetAccountAsync(CancellationToken cancellationToken)
    {
        var result = await RequestAsync("account/read", new { refreshToken = false }, cancellationToken);
        if (!result.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object)
        {
            return AccountState.NotSignedIn;
        }

        var type = account.TryGetProperty("type", out var rawType) ? rawType.GetString() : null;
        return new AccountState(type);
    }

    public async Task<QuotaSet> GetRateLimitsAsync(CancellationToken cancellationToken)
    {
        var result = await RequestAsync<object?>("account/rateLimits/read", null, cancellationToken);
        return QuotaSet.FromRateLimitsResponse(result);
    }

    private async Task<JsonElement> RequestAsync<TParams>(string method, TParams parameters, CancellationToken cancellationToken)
    {
        if (_input is null)
        {
            throw new InvalidOperationException("App Server is not running.");
        }

        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Unable to register App Server request.");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            await WriteAsync(new { id, method, @params = parameters }, timeout.Token);
            return await completion.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task SendNotificationAsync<TParams>(string method, TParams parameters, CancellationToken cancellationToken)
    {
        return WriteAsync(new { method, @params = parameters }, cancellationToken);
    }

    private async Task WriteAsync<TMessage>(TMessage message, CancellationToken cancellationToken)
    {
        if (_input is null)
        {
            throw new InvalidOperationException("App Server is not running.");
        }

        var line = JsonSerializer.Serialize(message);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await _input.WriteLineAsync(line.AsMemory(), cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task PumpOutputAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (_process is not null && !cancellationToken.IsCancellationRequested)
            {
                var line = await _process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                if (TryGetRequestId(root, out var id) && root.TryGetProperty("result", out var result))
                {
                    if (_pending.TryGetValue(id, out var completion))
                    {
                        completion.TrySetResult(result.Clone());
                    }

                    continue;
                }

                if (TryGetRequestId(root, out id) && root.TryGetProperty("error", out var error))
                {
                    if (_pending.TryGetValue(id, out var completion))
                    {
                        completion.TrySetException(new InvalidOperationException(error.GetRawText()));
                    }

                    continue;
                }

                if (root.TryGetProperty("method", out var rawMethod) && rawMethod.ValueKind == JsonValueKind.String)
                {
                    var method = rawMethod.GetString();
                    if (string.Equals(method, "account/rateLimits/updated", StringComparison.Ordinal))
                    {
                        RateLimitsUpdated?.Invoke(this, EventArgs.Empty);
                    }

                    if (TryGetRequestId(root, out id))
                    {
                        await WriteAsync(
                            new
                            {
                                id,
                                error = new
                                {
                                    code = -32000,
                                    message = "codex-quota is read-only and does not provide credentials or approvals."
                                }
                            },
                            cancellationToken);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FailPending(exception);
        }
        finally
        {
            FailPending(new InvalidOperationException("Codex App Server connection closed."));
        }
    }

    private async Task DrainErrorAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (_process is not null && !cancellationToken.IsCancellationRequested)
            {
                if (await _process.StandardError.ReadLineAsync(cancellationToken) is null)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static bool TryGetRequestId(JsonElement root, out int id)
    {
        id = default;
        return root.TryGetProperty("id", out var rawId) && rawId.TryGetInt32(out id);
    }

    private void FailPending(Exception exception)
    {
        foreach (var completion in _pending.Values)
        {
            completion.TrySetException(exception);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        _shutdown.Cancel();
        _input?.Close();

        if (_process is { HasExited: false } process)
        {
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        _input?.Dispose();
        _process?.Dispose();
        _writeGate.Dispose();
        _shutdown.Dispose();
    }
}

internal sealed record AccountState(string? Type)
{
    public static AccountState NotSignedIn { get; } = new((string?)null);

    public bool IsSignedInWithChatGpt => Type?.Contains("chatgpt", StringComparison.OrdinalIgnoreCase) == true;
}
