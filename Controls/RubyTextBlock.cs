using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
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
    private readonly TextBlock _textBlock;
    private readonly List<RubySegment> _segments = new();

    /// <summary>
    /// Estimated height overhead per line for ruby text, as a fraction of the base font size.
    /// Used by the caller for font-fit calculations.
    /// </summary>
    public const double RubyLineOverheadFactor = 0.55;

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
            // AntiqueWhite at 0.8 opacity — matches translatedTextBlock
            Background = new SolidColorBrush(Color.FromArgb(204, 250, 235, 215)),
        };

        AddVisualChild(_textBlock);
        AddLogicalChild(_textBlock);
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
        _textBlock.Measure(availableSize);
        return _textBlock.DesiredSize;
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        _textBlock.Arrange(new Rect(finalSize));
        return finalSize;
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
        _segments.Clear();
        _textBlock.Inlines.Clear();

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

    #endregion

    #region Private Helpers

    /// <summary>
    /// Adds a ruby group: a vertical StackPanel with reading TextBlock above surface TextBlock,
    /// wrapped in an InlineUIContainer for natural line wrapping.
    /// </summary>
    private void AddRubyGroup(string surface, string reading)
    {
        var rubyReading = new TextBlock
        {
            Text = reading,
            FontSize = BaseFontSize * 0.5,
            TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(Colors.DarkSlateGray),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var rubySurface = new TextBlock
        {
            Text = surface,
            FontSize = BaseFontSize,
            TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(Colors.DarkSlateGray),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(rubyReading);
        stack.Children.Add(rubySurface);

        var container = new InlineUIContainer(stack);
        _textBlock.Inlines.Add(container);
    }

    /// <summary>
    /// Adds a plain Run for pure-kana or reading-less segments.
    /// </summary>
    private void AddPlainRun(string text)
    {
        var run = new Run(text)
        {
            FontSize = BaseFontSize,
            Foreground = new SolidColorBrush(Colors.DarkSlateGray),
        };
        _textBlock.Inlines.Add(run);
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
}
