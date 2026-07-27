using System;
using System.Threading;
using System.Threading.Tasks;
using OnscreenTranslator.Core.Platform;

namespace OnscreenTranslator.Linux.Platform;

/// <summary>
/// Polls for hotkey combinations on a background thread.
/// For production use, integrate with X11 XGrabKey or sxhkd.
/// </summary>
internal class LinuxHotkeyRegistration : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly KeyModifiers _modifiers;
    private readonly VirtualKey _key;
    private readonly Action _callback;

    public LinuxHotkeyRegistration(KeyModifiers modifiers, VirtualKey key, Action callback)
    {
        _modifiers = modifiers;
        _key = key;
        _callback = callback;
        // TODO: Implement real hotkey polling via X11 or external process
        Task.Run(PollLoop, _cts.Token);
    }

    private async Task PollLoop()
    {
        // Stub: real implementation would use X11 XQueryKeymap or listen to /dev/input
        while (!_cts.IsCancellationRequested)
        {
            await Task.Delay(100, _cts.Token);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
