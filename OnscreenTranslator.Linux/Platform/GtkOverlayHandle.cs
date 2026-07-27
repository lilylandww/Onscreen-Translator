using System;
using System.Drawing;
using OnscreenTranslator.Core;
using OnscreenTranslator.Core.Platform;

namespace OnscreenTranslator.Linux.Platform;

/// <summary>
/// Overlay handle that creates a transparent, always-on-top GTK/Avalonia window
/// to display translation text and furigana over the captured region.
/// </summary>
internal class GtkOverlayHandle : IOverlayHandle
{
    private readonly Rectangle _region;
    private OverlayState? _state;

    public GtkOverlayHandle(Rectangle region)
    {
        _region = region;
        // TODO: Create a real Avalonia Popup/TopLevel overlay window
    }

    public void UpdateContent(OverlayState state)
    {
        _state = state;
        // TODO: Render text/furigana onto the overlay surface
    }

    public void SetBounds(Rectangle bounds)
    {
        // TODO: Move/resize the overlay window
    }

    public void SetVisible(bool visible)
    {
        // TODO: Show/hide the overlay window
    }

    public void Dispose()
    {
        // TODO: Destroy the overlay window
    }
}
