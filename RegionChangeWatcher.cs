using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace WpfAppTest;

/// <summary>
/// Watches a captured screen region for content changes while the translation
/// card is visible. Input events (scroll/click/key) trigger a debounced pixel
/// diff of the region; a poll timer additionally catches input-less changes
/// (video, animation) when capture excludes our own overlay.
/// The reference frame is the screenshot taken when the region was translated,
/// downsampled to a 64x64 grayscale grid; diffs are normalized mean absolute
/// differences in [0, 1].
/// </summary>
public sealed class RegionChangeWatcher : IDisposable
{
    private const int GridSize = 64;
    private const int DebounceMs = 150;
    private const int PollIntervalMs = 1000;

    /// <summary>Raised on the UI thread when the watched region has changed.</summary>
    public event Action? RegionChanged;

    /// <summary>
    /// Captures the region for a check. The callback owns any hide/restore of
    /// overlay chrome needed for a clean capture. Returns null on failure.
    /// </summary>
    public Func<Rectangle, Task<Bitmap?>>? CaptureRegionAsync { get; set; }

    public bool IsArmed { get; private set; }

    private readonly DispatcherTimer _debounceTimer;
    private readonly DispatcherTimer _pollTimer;

    private Rectangle _region;
    private float[]? _reference;
    private double _sensitivity;
    private bool _confirmWithPixels;
    private bool _checkRunning;
    private bool _pendingCheck;
    private bool _suspended;
    private bool _disposed;

    /// <summary>
    /// Single-check diffs below this are treated as compositor/encoding noise
    /// and may be rebased; anything above accumulates against the fixed
    /// reference taken at translation time.
    /// </summary>
    private const double NoiseFloor = 0.005;

    public RegionChangeWatcher(Dispatcher dispatcher)
    {
        _debounceTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(DebounceMs)
        };
        _debounceTimer.Tick += (_, _) => RunCheckAsync();

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(PollIntervalMs)
        };
        _pollTimer.Tick += (_, _) => RunCheckAsync();
    }

    /// <summary>
    /// Starts watching with the given reference frame (downsampled).
    /// <paramref name="confirmWithPixels"/> false = input-only invalidation
    /// (used when capture cannot see past our own card; a pixel check would
    /// race the DWM present and blink the card). <paramref name="enablePoll"/>
    /// only makes sense together with pixel confirmation.
    /// </summary>
    public void Arm(Rectangle region, float[] referenceFrame, double sensitivity, bool confirmWithPixels, bool enablePoll)
    {
        if (_disposed) return;

        _region = region;
        _reference = referenceFrame;
        _sensitivity = Math.Clamp(sensitivity, 0.005, 0.5);
        _confirmWithPixels = confirmWithPixels;
        IsArmed = true;
        _suspended = false;
        _pendingCheck = false;

        _pollTimer.Stop();
        if (enablePoll && confirmWithPixels)
            _pollTimer.Start();
    }

    public void Disarm()
    {
        IsArmed = false;
        _reference = null;
        _pendingCheck = false;
        _debounceTimer.Stop();
        _pollTimer.Stop();
    }

    /// <summary>Pauses checks without losing the reference (during card drag).</summary>
    public void Suspend() => _suspended = true;

    public void Resume() => _suspended = false;

    /// <summary>
    /// Call when system input that may have changed the screen occurred.
    /// Schedules a debounced check. The caller is responsible for filtering
    /// input that targets overlay UI itself.
    /// </summary>
    public void NotifyInput()
    {
        if (!IsArmed || _disposed || _suspended) return;

        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private async void RunCheckAsync()
    {
        if (!IsArmed || _disposed || _suspended || _reference == null) return;

        // Input-only mode (capture can't exclude our card): any debounced input
        // from the underlying app invalidates — the screen *may* have changed.
        if (!_confirmWithPixels)
        {
            Disarm();
            RegionChanged?.Invoke();
            return;
        }

        if (_checkRunning)
        {
            _pendingCheck = true;
            return;
        }

        _checkRunning = true;
        try
        {
            Bitmap? bmp = CaptureRegionAsync != null
                ? await CaptureRegionAsync(_region)
                : null;
            if (bmp == null)
                return;

            // Disarmed while the capture was in flight — drop the result.
            if (_reference == null || !IsArmed)
            {
                bmp.Dispose();
                return;
            }

            float[] current;
            using (bmp)
            {
                current = Downsample(bmp);
            }

            if (_reference == null || !IsArmed) return;

            double diff = MeanAbsDiff(_reference, current);
            if (diff >= _sensitivity)
            {
                Disarm();
                RegionChanged?.Invoke();
            }
            else if (diff < NoiseFloor)
            {
                // Rebase only genuine noise. Larger sub-threshold diffs must keep
                // accumulating against the fixed translation-time reference, or
                // slow scrolling/typing would never cross the threshold.
                _reference = current;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RegionChangeWatcher] Check failed: {ex.Message}");
        }
        finally
        {
            _checkRunning = false;
            if (_pendingCheck && IsArmed)
            {
                _pendingCheck = false;
                _debounceTimer.Stop();
                _debounceTimer.Start();
            }
        }
    }

    /// <summary>Downsamples a bitmap to a GridSize x GridSize grayscale grid (mean per cell).</summary>
    public static float[] Downsample(Bitmap bmp, int grid = GridSize)
    {
        float[] cells = new float[grid * grid];
        int[] counts = new int[grid * grid];

        BitmapData data = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* scan0 = (byte*)data.Scan0;
                for (int y = 0; y < bmp.Height; y++)
                {
                    byte* row = scan0 + y * data.Stride;
                    int gy = Math.Min(grid - 1, y * grid / bmp.Height);
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        byte* px = row + x * 4;
                        // BGRX layout; perceptual-ish luma.
                        float luma = 0.299f * px[2] + 0.587f * px[1] + 0.114f * px[0];
                        int gx = Math.Min(grid - 1, x * grid / bmp.Width);
                        int idx = gy * grid + gx;
                        cells[idx] += luma;
                        counts[idx]++;
                    }
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        for (int i = 0; i < cells.Length; i++)
            cells[i] = counts[i] > 0 ? cells[i] / counts[i] / 255f : 0f;

        return cells;
    }

    /// <summary>Normalized mean absolute difference between two downsampled frames, in [0, 1].</summary>
    public static double MeanAbsDiff(float[] a, float[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        if (len == 0) return 1.0;

        double sum = 0;
        for (int i = 0; i < len; i++)
            sum += Math.Abs(a[i] - b[i]);

        return sum / len;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounceTimer.Stop();
        _pollTimer.Stop();
        IsArmed = false;
    }
}
