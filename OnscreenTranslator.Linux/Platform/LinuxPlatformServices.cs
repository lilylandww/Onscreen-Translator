using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using OnscreenTranslator.Core.Platform;

namespace OnscreenTranslator.Linux.Platform;

/// <summary>
/// Linux implementation of IPlatformServices using X11/xdg tools.
/// Uses `import`/`maim`/`scrot` for screenshots, `xdotool` for key events,
/// and XOR-fallback for data protection (libsecret integration TBD).
/// </summary>
public class LinuxPlatformServices : IPlatformServices
{
    public Task<Bitmap> CaptureScreenAsync(Rectangle region)
    {
        return Task.Run(() =>
        {
            var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
            try
            {
                var tool = IsWayland()
                    ? (HasTool("grim") ? "grim" : throw new InvalidOperationException(
                        "Wayland detected but 'grim' is not installed. Install grim (sudo apt install grim) or use X11."))
                    : (HasTool("maim") ? "maim" : "import");
                var toolArgs = IsWayland()
                    ? $"-g \"{region.X},{region.Y} {region.Width}x{region.Height}\" {tmp}"
                    : tool == "maim"
                        ? $"-x {region.X} -y {region.Y} -w {region.Width} -h {region.Height} {tmp}"
                        : $"-silent -window root -crop {region.Width}x{region.Height}+{region.X}+{region.Y} {tmp}";

                RunCommand(tool, toolArgs);

                if (!File.Exists(tmp))
                    throw new InvalidOperationException($"Screenshot tool failed: {tool} {toolArgs}");

                var bitmap = new Bitmap(tmp);
                // Clone so we can delete the temp file
                var result = new Bitmap(bitmap);
                bitmap.Dispose();
                return result;
            }
            finally
            {
                try { File.Delete(tmp); } catch { /* best effort */ }
            }
        });
    }

    public Rectangle GetScreenBounds()
    {
        // Use xrandr to get screen geometry
        try
        {
            var output = RunCommandAndRead("xrandr");
            // Parse the "current W x H" line, e.g. "Screen 0: minimum 16 x 16, current 3840 x 2160"
            foreach (var line in output.Split('\n'))
            {
                if (line.Contains("current"))
                {
                    var parts = line.Split(',');
                    foreach (var part in parts)
                    {
                        var trimmed = part.Trim();
                        if (trimmed.StartsWith("current"))
                        {
                            var nums = trimmed.Replace("current", "").Trim().Split('x');
                            if (nums.Length == 2
                                && int.TryParse(nums[0].Trim(), out int w)
                                && int.TryParse(nums[1].Trim(), out int h))
                                return new Rectangle(0, 0, w, h);
                        }
                    }
                }
            }
        }
        catch { /* fall through */ }

        return new Rectangle(0, 0, 1920, 1080); // fallback
    }

    public IDisposable RegisterGlobalHotkey(KeyModifiers modifiers, VirtualKey key, Action callback)
    {
        // On Linux, global hotkeys are typically handled by the display server
        // or via external tools like `sxhkd`. For now, we use a simple polling
        // approach with X11 key grabs using a background thread.
        return new LinuxHotkeyRegistration(modifiers, key, callback);
    }

    public IOverlayHandle CreateOverlay(Rectangle region)
    {
        return new GtkOverlayHandle(region);
    }

    public byte[] ProtectData(byte[] data, byte[] entropy)
    {
        // XOR obfuscation fallback (same as ProtectedStringConverter.Fallback).
        // TODO: Integrate libsecret (Secret Service API) via DBus for real encryption.
        return XorCrypt(data, entropy);
    }

    public byte[] UnprotectData(byte[] data, byte[] entropy)
    {
        return XorCrypt(data, entropy); // XOR is symmetric
    }

    public IDisposable ShowSystemTrayIcon(string iconPath, SystemTrayMenu menu)
    {
        // Use libappindicator or the StatusNotifierItem DBus protocol.
        // For now, raise a placeholder that logs menu actions.
        return new LinuxTrayIcon(iconPath, menu);
    }

    public void RestoreMainWindow()
    {
        // The Avalonia window lifecycle handles this natively.
        Debug.WriteLine("RestoreMainWindow called — handled by Avalonia.");
    }

    // ── internal helpers ──────────────────────────────────────────────

    private static byte[] XorCrypt(byte[] data, byte[] key)
    {
        var result = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
            result[i] = (byte)(data[i] ^ key[i % key.Length]);
        return result;
    }

    private static bool HasTool(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("which", name)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(1000);
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string RunCommandAndRead(string command)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        proc.Start();
        // Read stderr concurrently to prevent pipe-buffer deadlock.
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(5000);
        try { stderrTask.GetAwaiter().GetResult(); } catch { /* swallow */ }
        return stdout;
    }

    private static void RunCommand(string command, string args)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        proc.Start();
        // Read stderr concurrently to prevent pipe-buffer deadlock.
        var stderrTask = proc.StandardError.ReadToEndAsync();
        proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(5000);
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"Command '{command} {args}' failed (exit {proc.ExitCode}): {stderr.Trim()}");
    }

    private static bool IsWayland()
    {
        return Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") != null
            || Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland";
    }
}
