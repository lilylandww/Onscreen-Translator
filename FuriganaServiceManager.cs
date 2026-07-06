using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WpfAppTest;

/// <summary>
/// Status of the furigana sidecar service, returned by <c>GET /status</c>.
/// </summary>
public record FuriganaServiceStatus(
    [property: JsonPropertyName("sudachi_ready")] bool SudachiReady,
    [property: JsonPropertyName("flfl_loaded")] bool FlflLoaded,
    [property: JsonPropertyName("flfl_loading")] bool FlflLoading,
    [property: JsonPropertyName("flfl_latency_ms")] double? FlflLatencyMs
);

/// <summary>
/// Event args for <see cref="FuriganaServiceManager.StatusChanged"/>.
/// </summary>
public class FuriganaServiceStatusChangedEventArgs : EventArgs
{
    public bool IsHealthy { get; init; }
    public FuriganaServiceStatus? Status { get; init; }
}

/// <summary>
/// Manages the lifecycle of the Python furigana sidecar process.
/// Handles start/stop, health checks, auto-restart on crash, and degradation.
/// </summary>
public class FuriganaServiceManager : IDisposable
{
    /// <summary>
    /// Shared singleton instance used by both MainWindow and SettingsWindow
    /// so that start/stop in Settings is reflected everywhere.
    /// </summary>
    private static FuriganaServiceManager? _instance;
    private static readonly object _instanceLock = new();
    public static FuriganaServiceManager Instance
    {
        get
        {
            lock (_instanceLock)
            {
                return _instance ??= new FuriganaServiceManager();
            }
        }
    }

    private Process? _process;
    private readonly object _lock = new();
    private bool _disposed;
    private bool _stopping;

    private const int HealthPollTimeoutMs = 30_000;
    private const int HealthPollIntervalMs = 500;
    private const int MaxRestartAttempts = 3;
    private static readonly int[] RestartBackoffDelaysMs = [1000, 4000, 15000];
    private const int GracefulShutdownTimeoutMs = 5000;

    private HttpClient? _httpClient;
    private int _restartAttempt;

    // --- Degradation monitoring ---
    private int _consecutiveSlowFlflCount;
    private bool _isDegraded;
    private Timer? _degradationPollTimer;

    /// <summary>
    /// True when FLFL has been auto-degraded and Ollama fallback should be used instead.
    /// </summary>
    public bool IsDegraded => _isDegraded;

    /// <summary>
    /// Fires when degradation state changes (FLFL→Ollama switch).
    /// </summary>
    public event EventHandler<bool>? DegradedChanged;

    /// <summary>
    /// Fires when the service health status changes.
    /// </summary>
    public event EventHandler<FuriganaServiceStatusChangedEventArgs>? StatusChanged;

    /// <summary>
    /// The URL the sidecar is bound to.
    /// </summary>
    public string SidecarUrl { get; private set; } = "http://127.0.0.1:8765";

    /// <summary>
    /// Whether the sidecar process is currently running.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _process != null && !_process.HasExited;
            }
        }
    }

    private HttpClient GetClient()
    {
        return _httpClient ??= new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
            BaseAddress = new Uri(SidecarUrl)
        };
    }

    /// <summary>
    /// Starts the sidecar process and waits for it to become healthy.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="degradationThresholdMs">FLFL latency threshold (ms) above which auto-degradation triggers.</param>
    /// <exception cref="TimeoutException">If the sidecar does not become healthy within 30 seconds.</exception>
    public async Task StartAsync(CancellationToken ct = default, int degradationThresholdMs = 2000)
    {
        if (IsRunning) return;

        // Determine the script to run
        bool isWindows = OperatingSystem.IsWindows();
        string scriptName = isWindows ? "run.bat" : "run.sh";
        string serviceDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "furigana-service");
        string scriptPath = Path.Combine(serviceDir, scriptName);

        if (!File.Exists(scriptPath))
        {
            Debug.WriteLine($"[FuriganaServiceManager] Sidecar script not found: {scriptPath}");
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = isWindows ? scriptPath : "/bin/bash",
            Arguments = isWindows ? "" : $"\"{scriptPath}\"",
            WorkingDirectory = serviceDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        lock (_lock)
        {
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null)
                    Debug.WriteLine($"[sidecar stdout] {e.Data}");
            };
            _process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null)
                    Debug.WriteLine($"[sidecar stderr] {e.Data}");
            };
            _process.Exited += OnProcessExited;

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        Debug.WriteLine($"[FuriganaServiceManager] Sidecar started (PID={_process.Id}). Polling /health...");

        // Poll /health until ready or timeout
        var deadline = DateTime.UtcNow.AddMilliseconds(HealthPollTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (await IsHealthyAsync())
            {
                Debug.WriteLine("[FuriganaServiceManager] Sidecar is healthy.");
                _restartAttempt = 0;
                StatusChanged?.Invoke(this, new FuriganaServiceStatusChangedEventArgs
                {
                    IsHealthy = true,
                    Status = await GetStatusAsync()
                });

                // Start degradation monitoring after sidecar is ready
                StartDegradationMonitoring(degradationThresholdMs);

                return;
            }

            if (IsRunning)
            {
                await Task.Delay(HealthPollIntervalMs, ct);
            }
            else
            {
                // Process exited before becoming healthy
                break;
            }
        }

        Debug.WriteLine("[FuriganaServiceManager] Sidecar failed to become healthy within timeout.");
        throw new TimeoutException("Furigana sidecar failed to become healthy within 30 seconds.");
    }

    /// <summary>
    /// Gracefully stops the sidecar process.
    /// </summary>
    public Task StopAsync()
    {
        _stopping = true;
        StopDegradationMonitoring();

        Process? process;
        lock (_lock)
        {
            process = _process;
            _process = null;
        }

        if (process == null || process.HasExited)
            return Task.CompletedTask;

        Debug.WriteLine($"[FuriganaServiceManager] Stopping sidecar (PID={process.Id})...");

        try
        {
            process.Exited -= OnProcessExited;
            process.CloseMainWindow();

            // Wait for graceful exit
            if (!process.WaitForExit(GracefulShutdownTimeoutMs))
            {
                Debug.WriteLine("[FuriganaServiceManager] Graceful shutdown timed out, killing process.");
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* process already exited */ }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FuriganaServiceManager] Error stopping sidecar: {ex.Message}");
        }
        finally
        {
            process.Dispose();
        }

        StatusChanged?.Invoke(this, new FuriganaServiceStatusChangedEventArgs
        {
            IsHealthy = false,
            Status = null
        });

        return Task.CompletedTask;
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (_stopping) return;

        Process? process;
        lock (_lock)
        {
            process = _process;
        }

        int exitCode = -1;
        try { exitCode = process?.ExitCode ?? -1; } catch { }

        Debug.WriteLine($"[FuriganaServiceManager] Sidecar exited unexpectedly (exit code: {exitCode}).");

        StatusChanged?.Invoke(this, new FuriganaServiceStatusChangedEventArgs
        {
            IsHealthy = false,
            Status = null
        });

        // Auto-restart with exponential backoff
        _ = AttemptRestartAsync();
    }

    private async Task AttemptRestartAsync()
    {
        if (_stopping || _restartAttempt >= MaxRestartAttempts)
        {
            Debug.WriteLine($"[FuriganaServiceManager] Not restarting (stopping={_stopping}, attempt={_restartAttempt}).");
            return;
        }

        int delay = RestartBackoffDelaysMs[Math.Min(_restartAttempt, RestartBackoffDelaysMs.Length - 1)];
        _restartAttempt++;
        Debug.WriteLine($"[FuriganaServiceManager] Restart attempt {_restartAttempt}/{MaxRestartAttempts} in {delay}ms...");

        await Task.Delay(delay);

        try
        {
            await StartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FuriganaServiceManager] Restart attempt {_restartAttempt} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks whether the sidecar is healthy by calling <c>GET /health</c>.
    /// </summary>
    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            var response = await GetClient().GetAsync("/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the current sidecar status by calling <c>GET /status</c>.
    /// </summary>
    public async Task<FuriganaServiceStatus?> GetStatusAsync()
    {
        try
        {
            var response = await GetClient().GetAsync("/status");
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<FuriganaServiceStatus>(body);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FuriganaServiceManager] Error getting status: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Instructs the sidecar to stop loading FLFL and switch to Ollama degradation.
    /// </summary>
    public async Task DegradeToOllamaAsync()
    {
        try
        {
            var payload = new { target = "ollama" };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await GetClient().PostAsync("/degrade", content);
            response.EnsureSuccessStatusCode();
            Debug.WriteLine("[FuriganaServiceManager] Degradation to Ollama requested.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FuriganaServiceManager] Error degrading to Ollama: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts a periodic poll that monitors FLFL latency and auto-degrades to Ollama
    /// when FLFL consistently exceeds the given threshold.
    /// </summary>
    /// <param name="thresholdMs">Latency threshold in ms (default 2000).</param>
    public void StartDegradationMonitoring(int thresholdMs = 2000)
    {
        StopDegradationMonitoring(); // dispose any existing timer

        _degradationPollTimer = new Timer(async _ =>
        {
            try
            {
                var status = await GetStatusAsync();
                if (status?.FlflLatencyMs.HasValue == true && status.FlflLatencyMs.Value > thresholdMs)
                {
                    _consecutiveSlowFlflCount++;
                    Debug.WriteLine(
                        $"[FuriganaServiceManager] FLFL slow ({status.FlflLatencyMs.Value:F0}ms > {thresholdMs}ms). " +
                        $"Consecutive slow count: {_consecutiveSlowFlflCount}");
                }
                else
                {
                    _consecutiveSlowFlflCount = 0;
                }

                if (_consecutiveSlowFlflCount >= 3 && !_isDegraded)
                {
                    _isDegraded = true;
                    await DegradeToOllamaAsync();
                    Debug.WriteLine("[FuriganaServiceManager] Auto-degraded to Ollama (FLFL slow).");
                    DegradedChanged?.Invoke(this, true);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FuriganaServiceManager] Degradation poll error: {ex.Message}");
            }
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Stops the degradation monitoring timer.
    /// </summary>
    public void StopDegradationMonitoring()
    {
        _degradationPollTimer?.Dispose();
        _degradationPollTimer = null;
    }

    /// <summary>
    /// Resets degradation state so FLFL is used again. Called by the user from Settings
    /// to re-enable FLFL after a previous auto-degradation.
    /// </summary>
    public void ResetDegradation()
    {
        _isDegraded = false;
        _consecutiveSlowFlflCount = 0;
        Debug.WriteLine("[FuriganaServiceManager] Degradation reset — FLFL re-enabled.");
        DegradedChanged?.Invoke(this, false);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _stopping = true;
            StopDegradationMonitoring();
            _httpClient?.Dispose();
            _httpClient = null;

            // Stop the process synchronously on dispose
            lock (_lock)
            {
                if (_process != null && !_process.HasExited)
                {
                    try
                    {
                        _process.Exited -= OnProcessExited;
                        _process.CloseMainWindow();
                        if (!_process.WaitForExit(GracefulShutdownTimeoutMs))
                        {
                            try { _process.Kill(entireProcessTree: true); } catch { }
                        }
                    }
                    catch { }
                    finally
                    {
                        _process.Dispose();
                        _process = null;
                    }
                }
            }

            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
