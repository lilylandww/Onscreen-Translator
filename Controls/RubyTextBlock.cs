using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using WpfAppTest.Providers.Breakdown;
using WpfAppTest.Providers.Furigana;

namespace WpfAppTest;

/// <summary>
/// A segment for ruby rendering with a surface string and optional hiragana reading.
/// </summary>
public class RubySegment
{
    /// <summary>The surface text (e.g. kanji + kana).</summary>
    public string Surface { get; set; } = "";

    /// <summary>The hiragana reading, or null/empty for pure-kana segments.</summary>
    public string? Reading { get; set; }
}

/// <summary>
/// Custom WPF control that renders Japanese text with hiragana ruby (furigana) above kanji runs.
/// Pure-kana segments render inline at base font size with no ruby row.
/// Uses a TextBlock with Inlines for native text wrapping support.
/// </summary>
/// <remarks>
/// Usage in XAML (with xmlns:local="clr-namespace:WpfAppTest"):
///   <code><![CDATA[<local:RubyTextBlock x:Name="rubyTextBlock" BaseFontSize="16" Visibility="Collapsed" />]]></code>
///
/// Usage in code-behind:
///   <code><![CDATA[rubyTextBlock.BaseFontSize = 16;
///   rubyTextBlock.SetSegments(furiganaSegments);]]></code>
/// </remarks>
public class RubyTextBlock : FrameworkElement
{
    private static readonly Brush _overlayBackgroundBrush = CreateOverlayBrush();
    private static readonly Brush _hoverHighlightBrush = CreateHoverHighlightBrush();
    private static readonly Brush _popupBackgroundBrush = CreatePopupBackgroundBrush();
    private static readonly Brush _popupBorderBrush = CreatePopupBorderBrush();
    private static readonly Brush _popupPrimaryTextBrush = CreateFrozenBrush(Color.FromRgb(84, 105, 115));      // DarkSlateGray
    private static readonly Brush _popupSecondaryTextBrush = CreateFrozenBrush(Color.FromRgb(120, 120, 120));   // Gray
    private static readonly Brush _grammarUnderlineBrush = CreateFrozenBrush(Color.FromRgb(30, 144, 255));       // DodgerBlue
    private static readonly TextDecorationCollection? GrammarUnderline = CreateGrammarUnderline();

    private readonly TextBlock _textBlock;
    private readonly List<RubySegment> _segments = new();

    // Hover state for breakdown parts (Breakdown view only).
    private Popup? _hoverPopup;
    private DispatcherTimer? _hoverDelayTimer;
    private Panel? _hoverTarget;
    private BreakdownPart? _hoverPart;

    /// <summary>
    /// Estimated height overhead per line for ruby text, as a fraction of the base font size.
    /// Used by the caller for font-fit calculations.
    /// </summary>
    public const double RubyLineOverheadFactor = 0.55;

    private static Brush CreateOverlayBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(204, 250, 235, 215)); // AntiqueWhite at 0.8 opacity
        brush.Freeze();
        return brush;
    }

    private static Brush CreateHoverHighlightBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(70, 30, 144, 255)); // DodgerBlue tint
        brush.Freeze();
        return brush;
    }

    private static Brush CreatePopupBackgroundBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(252, 255, 253, 245));
        brush.Freeze();
        return brush;
    }

    private static Brush CreatePopupBorderBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(255, 200, 206, 210));
        brush.Freeze();
        return brush;
    }

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static TextDecorationCollection? CreateGrammarUnderline()
    {
        try
        {
            var pen = new Pen(_grammarUnderlineBrush, 1.0) { DashStyle = DashStyles.Dot };
            var decoration = new TextDecoration
            {
                Location = TextDecorationLocation.Underline,
                Pen = pen
            };
            return new TextDecorationCollection { decoration };
        }
        catch (Exception)
        {
            // Fall back to a solid underline if the dotted pen cannot be built.
            return TextDecorations.Underline;
        }
    }

    #region Dependency Properties

    public static readonly DependencyProperty BaseFontSizeProperty =
        DependencyProperty.Register(
            nameof(BaseFontSize),
            typeof(double),
            typeof(RubyTextBlock),
            new FrameworkPropertyMetadata(
                16.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange,
                OnBaseFontSizeChanged));

    /// <summary>
    /// The base font size for kanji/surface text. Ruby readings use ~50% of this size.
    /// </summary>
    public double BaseFontSize
    {
        get => (double)GetValue(BaseFontSizeProperty);
        set => SetValue(BaseFontSizeProperty, value);
    }

    private static void OnBaseFontSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RubyTextBlock ruby)
        {
            ruby._textBlock.FontSize = (double)e.NewValue;
        }
    }

    #endregion

    /// <summary>
    /// Creates a new <see cref="RubyTextBlock"/> control.
    /// </summary>
    public RubyTextBlock()
    {
        _textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(Colors.DarkSlateGray),
        };

        AddVisualChild(_textBlock);
        AddLogicalChild(_textBlock);

        // Dismiss any open hover popup when the control is hidden (view switch).
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue)
            {
                _hoverDelayTimer?.Stop();
                _hoverTarget = null;
                _hoverPart = null;
                HideHoverPopup();
            }
        };
    }

    #region Visual Tree

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index)
    {
        if (index != 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _textBlock;
    }

    #endregion

    #region Layout

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        _textBlock.FontSize = BaseFontSize;
        _textBlock.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return new Size(
            Math.Max(_textBlock.DesiredSize.Width, availableSize.Width),
            Math.Max(_textBlock.DesiredSize.Height, availableSize.Height));
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        // Center the text vertically WITHOUT mutating Padding (mutating a
        // measure-affecting property during/after measure caused WPF to re-run
        // layout and the block to jump up and down). Arranging at a computed
        // offset is stable across passes.
        double desiredHeight = _textBlock.DesiredSize.Height;
        double y = Math.Max(0, (finalSize.Height - desiredHeight) / 2);
        _textBlock.Arrange(new Rect(0, y, finalSize.Width, Math.Max(desiredHeight, 0)));
        return finalSize;
    }

    /// <summary>
    /// Paints the overlay background across the full arranged bounds. Drawn here
    /// (rather than on the inner <c>TextBlock</c>) so the background covers the
    /// whole region while the text is centered independently.
    /// </summary>
    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawRectangle(_overlayBackgroundBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        base.OnRender(drawingContext);
    }

    #endregion

    #region Public API

    /// <summary>
    /// Rebuilds the ruby rendering from furigana segments.
    /// </summary>
    /// <param name="segments">
    /// The morphological segments from <see cref="IFuriganaProvider"/>.
    /// Segments with a non-empty <c>Reading</c> AND a surface containing kanji
    /// are rendered as vertical ruby groups (reading above surface).
    /// Pure-kana segments or segments without a reading are rendered as plain inline text.
    /// </param>
    public void SetSegments(IEnumerable<FuriganaSegment> segments)
    {
        ResetSegments();

        foreach (var segment in segments)
        {
            _segments.Add(new RubySegment
            {
                Surface = segment.Surface,
                Reading = segment.Reading
            });

            bool hasReading = !string.IsNullOrEmpty(segment.Reading);
            bool surfaceHasKanji = ContainsKanji(segment.Surface);

            if (hasReading && surfaceHasKanji)
            {
                AddRubyGroup(segment.Surface, segment.Reading!);
            }
            else
            {
                AddPlainRun(segment.Surface);
            }
        }
    }

    /// <summary>
    /// Rebuilds the rendering from sentence-breakdown parts (Breakdown view).
    /// Each part renders like a ruby segment (reading above surface when kanji
    /// is present) and becomes hoverable: highlight tint + dotted underline for
    /// grammar parts, plus a minimal popup showing reading + meaning.
    /// </summary>
    /// <param name="parts">Vocab + grammar parts; surfaces must concatenate to the original sentence.</param>
    public void SetBreakdownParts(IReadOnlyList<BreakdownPart> parts)
    {
        ResetSegments();

        foreach (var part in parts)
        {
            _segments.Add(new RubySegment
            {
                Surface = part.Surface,
                Reading = part.Reading
            });

            bool hasReading = !string.IsNullOrEmpty(part.Reading);
            bool surfaceHasKanji = ContainsKanji(part.Surface);

            if (hasReading && surfaceHasKanji)
            {
                AddRubyGroup(part.Surface, part.Reading!, part);
            }
            else
            {
                AddPlainRun(part.Surface, part);
            }
        }
    }

    /// <summary>
    /// Clears rendered segments and any active hover popup/timer state.
    /// </summary>
    private void ResetSegments()
    {
        _hoverDelayTimer?.Stop();
        _hoverTarget = null;
        _hoverPart = null;
        HideHoverPopup();

        _segments.Clear();
        _textBlock.Inlines.Clear();
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Adds a ruby group: a two-row Grid with reading TextBlock above surface TextBlock,
    /// wrapped in an InlineUIContainer for natural line wrapping.
    /// The first row has a fixed height matching the ruby reading font size to ensure
    /// consistent vertical alignment across all segments.
    /// When <paramref name="hoverPart"/> is provided (Breakdown view), the group becomes hoverable.
    /// </summary>
    private void AddRubyGroup(string surface, string reading, BreakdownPart? hoverPart = null)
    {
        double rubyFontSize = BaseFontSize * 0.5;

        var rubyReading = new TextBlock
        {
            Text = reading,
            FontSize = rubyFontSize,
            TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(Colors.DarkSlateGray),
            LineHeight = rubyFontSize * 1.1,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };

        var rubySurface = new TextBlock
        {
            Text = surface,
            FontSize = BaseFontSize,
            TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(Colors.DarkSlateGray),
        };

        var grid = CreateRubyGrid();
        grid.Children.Add(rubyReading);
        grid.Children.Add(rubySurface);
        Grid.SetRow(rubyReading, 0);
        Grid.SetRow(rubySurface, 1);

        if (hoverPart != null)
            AttachBreakdownHover(grid, rubySurface, hoverPart);

        var container = new InlineUIContainer(grid) { BaselineAlignment = BaselineAlignment.Bottom };
        _textBlock.Inlines.Add(container);
    }

    /// <summary>
    /// Adds a plain-text segment wrapped in the same two-row Grid structure as ruby groups.
    /// The first row is empty but has the same fixed height as the ruby reading row,
    /// ensuring plain-kana segments align vertically with ruby-annotated segments.
    /// When <paramref name="hoverPart"/> is provided (Breakdown view), the group becomes hoverable.
    /// </summary>
    private void AddPlainRun(string text, BreakdownPart? hoverPart = null)
    {
        var surface = new TextBlock
        {
            Text = text,
            FontSize = BaseFontSize,
            Foreground = new SolidColorBrush(Colors.DarkSlateGray),
        };

        var grid = CreateRubyGrid();
        grid.Children.Add(surface);
        Grid.SetRow(surface, 1);

        if (hoverPart != null)
            AttachBreakdownHover(grid, surface, hoverPart);

        var container = new InlineUIContainer(grid) { BaselineAlignment = BaselineAlignment.Bottom };
        _textBlock.Inlines.Add(container);
    }

    /// <summary>
    /// Creates a two-row Grid with a fixed-height first row for ruby reading space
    /// and an auto-height second row for the surface text. This shared structure
    /// ensures all segments (ruby and plain) have uniform vertical dimensions.
    /// </summary>
    private Grid CreateRubyGrid()
    {
        double rubyRowHeight = BaseFontSize * 0.5 * 1.1; // matches ruby reading LineHeight

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(rubyRowHeight, GridUnitType.Pixel) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return grid;
    }

    /// <summary>
    /// Returns true if the text contains any CJK Unified Ideograph characters
    /// (kanji) that would warrant ruby annotations.
    /// </summary>
    private static bool ContainsKanji(string text)
    {
        foreach (char c in text)
        {
            // CJK Unified Ideographs
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
            // CJK Extension A
            if (c >= 0x3400 && c <= 0x4DBF) return true;
            // CJK Compatibility Ideographs
            if (c >= 0xF900 && c <= 0xFAFF) return true;
        }
        return false;
    }

    #endregion

    #region Breakdown Hover

    /// <summary>
    /// Makes a breakdown segment hoverable: highlight tint on enter, grammar
    /// parts get a dotted underline, and after a short delay a minimal popup
    /// shows the reading + meaning.
    /// </summary>
    private void AttachBreakdownHover(Panel target, TextBlock surface, BreakdownPart part)
    {
        // Transparent background makes the whole segment (incl. the empty ruby
        // row) hit-testable — without it only the TextBlocks would be hovered.
        target.Background = Brushes.Transparent;
        target.Cursor = Cursors.Arrow;

        if (part.Kind == BreakdownPartKind.Grammar && GrammarUnderline != null)
            surface.TextDecorations = GrammarUnderline;

        target.MouseEnter += (_, _) => OnBreakdownPartEnter(target, part);
        target.MouseLeave += (_, _) => OnBreakdownPartLeave(target);
    }

    private void OnBreakdownPartEnter(Panel target, BreakdownPart part)
    {
        // Moving directly between parts can fire Enter(B) before Leave(A);
        // the popup always belongs to the currently hovered part only.
        HideHoverPopup();

        target.Background = _hoverHighlightBrush;
        _hoverTarget = target;
        _hoverPart = part;

        _hoverDelayTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _hoverDelayTimer.Stop();
        _hoverDelayTimer.Tick -= OnHoverDelayTick;
        _hoverDelayTimer.Tick += OnHoverDelayTick;
        _hoverDelayTimer.Start();
    }

    private void OnBreakdownPartLeave(Panel target)
    {
        target.Background = Brushes.Transparent;

        if (!ReferenceEquals(_hoverTarget, target))
            return;

        _hoverTarget = null;
        _hoverPart = null;
        _hoverDelayTimer?.Stop();
        HideHoverPopup();
    }

    private void OnHoverDelayTick(object? sender, EventArgs e)
    {
        _hoverDelayTimer?.Stop();

        var target = _hoverTarget;
        var part = _hoverPart;
        if (target == null || part == null || !IsVisible)
            return;

        // Nothing informative to show — keep the highlight only.
        if (string.IsNullOrEmpty(part.Meaning) && string.IsNullOrEmpty(part.Reading))
            return;

        ShowHoverPopup(part, target);
    }

    private void ShowHoverPopup(BreakdownPart part, Panel target)
    {
        HideHoverPopup();

        _hoverPopup = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.Top,
            VerticalOffset = -6,
            StaysOpen = true,
            AllowsTransparency = true,
            Child = BuildHoverPopupChild(part),
            IsOpen = true
        };
    }

    private void HideHoverPopup()
    {
        if (_hoverPopup == null)
            return;

        _hoverPopup.IsOpen = false;
        _hoverPopup.Child = null;
        _hoverPopup = null;
    }

    /// <summary>
    /// Builds the minimal hover card: reading (kana, small, secondary color)
    /// above the meaning (English gloss / grammar function). Non-interactive.
    /// </summary>
    private static Border BuildHoverPopupChild(BreakdownPart part)
    {
        var panel = new StackPanel();

        if (!string.IsNullOrEmpty(part.Reading))
        {
            panel.Children.Add(new TextBlock
            {
                Text = part.Reading,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 1),
                Foreground = _popupSecondaryTextBrush
            });
        }

        if (!string.IsNullOrEmpty(part.Meaning))
        {
            panel.Children.Add(new TextBlock
            {
                Text = part.Meaning,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = _popupPrimaryTextBrush
            });
        }

        var border = new Border
        {
            Background = _popupBackgroundBrush,
            BorderBrush = _popupBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 5, 8, 5),
            MaxWidth = 280,
            Child = panel,
            // Non-interactive: hovers/clicks pass through, so a clamped popup
            // overlapping the part can't steal MouseLeave and cause flicker.
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 8,
                ShadowDepth = 2,
                Opacity = 0.35
            }
        };
        return border;
    }

    #endregion
}
