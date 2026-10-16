using System.Collections.Generic;
using System.Drawing;
using WpfAppTest.Providers.Breakdown;
using WpfAppTest.Providers.Furigana;

namespace WpfAppTest;

/// <summary>
/// The view modes available for the overlay.
/// </summary>
public enum OverlayViewMode
{
    Translation,
    Furigana,
    Breakdown,
    Original
}

/// <summary>
/// Tracks the state of the current overlay region, including OCR text,
/// translation, furigana segments, and the active view mode.
/// This is a single-instance class shared across the app for the currently selected region.
/// </summary>
public class OverlayState
{
    /// <summary>The raw OCR text for the current region.</summary>
    public string OcrText { get; set; } = "";

    /// <summary>The translated text for the current region.</summary>
    public string Translation { get; set; } = "";

    /// <summary>Furigana segments for the current region.</summary>
    public List<FuriganaSegment> FuriganaSegments { get; set; } = new();

    /// <summary>Vocab + grammar parts for the current region (Breakdown view).</summary>
    public List<BreakdownPart> BreakdownParts { get; set; } = new();

    /// <summary>The currently active view mode.</summary>
    public OverlayViewMode CurrentView { get; set; } = OverlayViewMode.Translation;

    /// <summary>The screen region rectangle that was captured.</summary>
    public Rectangle? Region { get; set; }

    /// <summary>Canvas X position for the overlay text block.</summary>
    public double X { get; set; }

    /// <summary>Canvas Y position for the overlay text block.</summary>
    public double Y { get; set; }

    /// <summary>User drag offset (DIP) applied on top of the region-anchored X position.</summary>
    public double CardOffsetX { get; set; }

    /// <summary>User drag offset (DIP) applied on top of the region-anchored Y position.</summary>
    public double CardOffsetY { get; set; }

    /// <summary>Deep-copies the state (used for overlay-mode redraw snapshots).</summary>
    public OverlayState Clone() => new()
    {
        OcrText = OcrText,
        Translation = Translation,
        FuriganaSegments = new List<FuriganaSegment>(FuriganaSegments),
        CurrentView = CurrentView,
        Region = Region,
        X = X,
        Y = Y,
        CardOffsetX = CardOffsetX,
        CardOffsetY = CardOffsetY
    };
}
