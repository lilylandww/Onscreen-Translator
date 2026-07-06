using Dapplo.Windows.User32;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using WpfAppTest.Utilities;
using WpfAppTest.Extensions;
using WpfAppTest.Providers;
using WpfAppTest.Providers.Furigana;
using System.Drawing;
using System.Drawing.Imaging;
using System.Collections.Generic;
using System.Linq;

namespace WpfAppTest
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private bool isSelecting = false;
        private Border selectBorder = new();
        private System.Windows.Point clickedPoint = new();
        private DisplayInfo? CurrentScreen { get; set; }
        private string? OCRText { get; set; }
        private string? TranslatedText { get; set; }
        private TextBox? editTextBox;
        private bool isEditing = false;
        private bool captureModeEnabled = true;

        private int currentScreenIndex = 0;
        private SystemTrayIcon? _systemTrayIcon;
        private WindowState _previousWindowState = WindowState.Maximized;

        private AppSettings _settings;
        private IOCRProvider _currentOcrProvider;
        private ITranslationProvider _currentTranslationProvider;
        private readonly List<ITranslationProvider> _translationProviders = [];
        private bool _initializing = true;
        private SettingsWindow? _settingsWindow;
        private CancellationTokenSource? _ocrCts;
        private readonly FuriganaServiceManager _furiganaServiceManager = new();
        private readonly OverlayState _overlayState = new();
        private CancellationTokenSource? _furiganaCts;
        private HttpFuriganaProvider? _furiganaProvider;

        private async Task<string> GetTranslatedTextAsync(string text)
        {
            try
            {
                return await _currentTranslationProvider.TranslateAsync(text);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Translation error: {ex.Message}");
                return $"Translation Failed: {ex.InnerException?.Message ?? ex.Message}";
            }
        }

        private async Task<string> GetOCRTextAsync(string imagePath)
        {
            try
            {
                _ocrCts ??= new CancellationTokenSource();
                return await _currentOcrProvider.RecognizeTextAsync(imagePath, _ocrCts.Token);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("OCR cancelled — provider was swapped.");
                return string.Empty;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OCR error: {ex.Message}");
                return string.Empty;
            }
        }

        public MainWindow()
        {
            InitializeComponent();

            _settings = AppSettings.Load();

            // Load API keys from environment for backward compatibility
            _ = DotNetEnv.Env.Load();
            var envGoogleKey = Environment.GetEnvironmentVariable("GOOGLE_API_KEYS");
            var envDeepLKey = Environment.GetEnvironmentVariable("DEEPL_API_KEY");

            // Migrate env keys into settings if settings don't have them
            var googleConfig = _settings.GetProviderConfig("google");
            if (string.IsNullOrEmpty(googleConfig.ApiKey) && !string.IsNullOrEmpty(envGoogleKey))
                googleConfig.ApiKey = envGoogleKey;

            var deeplConfig = _settings.GetProviderConfig("deepl");
            if (string.IsNullOrEmpty(deeplConfig.ApiKey) && !string.IsNullOrEmpty(envDeepLKey))
                deeplConfig.ApiKey = envDeepLKey;

            // Create providers using the factory
            _currentOcrProvider = ProviderFactory.CreateOcrProvider(_settings);
            _translationProviders = ProviderFactory.CreateTranslationProviders(_settings);

            _currentTranslationProvider = _translationProviders.Find(p => p.Name == _settings.TranslationProvider)
                ?? _translationProviders[0];

            StateChanged += Window_StateChanged;
        }

        public void SetImageToBackground()
        {
            BG.Source = null;
            BG.Source = ImageMethods.GetWindowBoundsImage(this);
            BackgroundBrush.Opacity = 0.2;
        }

        internal void KeyPressed(Key key, bool? isActive = null)
        {
            switch (key)
            {
                case Key.Escape:
                    MinimizeWindow();
                    break;
                default:
                    break;
            }
        }

        private void HandleKeyDown(object sender, KeyEventArgs e)
        {
            KeyPressed(e.Key);
        }

        private void CancelItemClick(object sender, RoutedEventArgs e)
        {
            Quit();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            OpenSettings();
        }

        private void OpenSettings()
        {
            if (_settingsWindow != null && _settingsWindow.IsLoaded)
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow(_settings);
            _settingsWindow.SettingsChanged += OnSettingsChanged;
            _settingsWindow.Closed += (s, e) =>
            {
                if (_settingsWindow != null)
                    _settingsWindow.SettingsChanged -= OnSettingsChanged;
                _settingsWindow = null;
            };
            _settingsWindow.Show();
        }

        private async void OnSettingsChanged()
        {
            // Reload settings from disk
            _settings = AppSettings.Load();

            // Cancel any in-flight OCR before swapping providers
            _ocrCts?.Cancel();
            _ocrCts?.Dispose();
            _ocrCts = null;
            _furiganaCts?.Cancel();
            _furiganaCts?.Dispose();
            _furiganaCts = null;
            // Dispose cached furigana provider (URL may have changed)
            _furiganaProvider?.Dispose();
            _furiganaProvider = null;

            // Dispose old providers
            if (_currentOcrProvider is IDisposable ocrDisposable)
                ocrDisposable.Dispose();
            foreach (var provider in _translationProviders)
            {
                if (provider is IDisposable disposable)
                    disposable.Dispose();
            }
            _translationProviders.Clear();

            // Recreate providers with new settings
            _currentOcrProvider = ProviderFactory.CreateOcrProvider(_settings);
            var newProviders = ProviderFactory.CreateTranslationProviders(_settings);
            _translationProviders.AddRange(newProviders);
            _currentTranslationProvider = _translationProviders.Find(p => p.Name == _settings.TranslationProvider)
                ?? _translationProviders[0];

            // Refresh toolbar UI
            _initializing = true;
            InitializeTranslationProviderComboBox();
            await LoadOllamaOcrModelsAsync();
            _initializing = false;

            Debug.WriteLine("Settings applied — providers refreshed.");
        }

        private void MinimizeWindow()
        {
            WindowState = WindowState.Minimized;
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                ShowInTaskbar = false;
                _systemTrayIcon?.Show();
            }
            else
            {
                _previousWindowState = WindowState;
                ShowInTaskbar = true;
                _systemTrayIcon?.Hide();
            }
        }

        private void RestoreFromTray()
        {
            ShowInTaskbar = true;
            WindowState = _previousWindowState;
            Show();
            Activate();
        }

        private void Canvas_MouseEnter(object sender, MouseEventArgs e)
        {
            TopButtonStack.Visibility = Visibility.Visible;
        }

        private void Canvas_MouseLeave(object sender, MouseEventArgs e)
        {
            TopButtonStack.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Handles the MouseDown event on the canvas, initiating a selection process.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The MouseButtonEventArgs instance containing the event data.</param>
        private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;

            // Don't initiate capture if capture mode is disabled
            if (!captureModeEnabled) return;

            isSelecting = true;
            TopButtonStack.Visibility = Visibility.Collapsed;
            vancas.CaptureMouse();
            CursorClipper.ClipCursor(this);
            clickedPoint = e.GetPosition(this);
            selectBorder.Height = 2;
            selectBorder.Width = 2;
            translatedTextBlock.Text = "";

            try { vancas.Children.Remove(selectBorder); } catch (Exception) { }
            selectBorder.BorderThickness = new Thickness(2);
            System.Windows.Media.Color borderColor = System.Windows.Media.Color.FromArgb(255, 40, 118, 126);
            selectBorder.BorderBrush = new SolidColorBrush(borderColor);
            _ = vancas.Children.Add(selectBorder);
            Canvas.SetLeft(selectBorder, clickedPoint.X);
            Canvas.SetTop(selectBorder, clickedPoint.Y);

            ApplicationUtilities.GetMousePosition(out System.Windows.Point mousePoint);
            foreach (DisplayInfo? screen in DisplayInfo.AllDisplayInfos)
            {
                Rect bound = screen.ScaledBounds();
                if (bound.Contains(mousePoint)) CurrentScreen = screen;
            }
        }

        private async void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!isSelecting) return;

            isSelecting = false;
            CurrentScreen = null;
            CursorClipper.UnClipCursor();
            vancas.ReleaseMouseCapture();
            clippingGeometry.Rect = new Rect(new System.Windows.Point(0, 0), new System.Windows.Size(0, 0));

            System.Windows.Point currentPoint = e.GetPosition(this);
            Matrix m = PresentationSource.FromVisual(this).CompositionTarget.TransformToDevice;
            currentPoint.X *= m.M11;
            currentPoint.Y *= m.M22;

            currentPoint.X = Math.Round(currentPoint.X);
            currentPoint.Y = Math.Round(currentPoint.Y);

            // Get the window's absolute position on the virtual screen
            System.Windows.Point windowPos = this.GetAbsolutePosition();

            double xDimension = Canvas.GetLeft(selectBorder) * m.M11;
            double yDimension = Canvas.GetTop(selectBorder) * m.M22;

            // Add window offset to get absolute screen coordinates
            Rectangle scaledRegion = new(
                (int)(windowPos.X + xDimension),
                (int)(windowPos.Y + yDimension),
                (int)(selectBorder.Width * m.M11),
                (int)(selectBorder.Height * m.M22));

            Bitmap bmp = ImageMethods.GetRegionOfScreenAsBitmap(scaledRegion);
            string timeStamp = ApplicationUtilities.GetTimestamp(DateTime.Now);
            bool isSmallArea = scaledRegion.Width < 5 && scaledRegion.Height < 5;
            if (isSmallArea)
            {
                BackgroundBrush.Opacity = 0;
                return;
            }
            string outputFileName = $"./output/{timeStamp}.png";
            bmp.Save(outputFileName, ImageFormat.Png);
            string text = await GetOCRTextAsync(outputFileName);
            OCRText = text;

            // Store OCR text and region in overlay state
            _overlayState.OcrText = text;
            _overlayState.Region = scaledRegion;
            _overlayState.X = xDimension;
            _overlayState.Y = yDimension;
            _overlayState.CurrentView = OverlayViewMode.Translation;

            // Reset toggle to translation view
            if (FuriganaToggleButton.IsChecked == true)
                FuriganaToggleButton.IsChecked = false;

            // Kick off furigana fetch in parallel with translation (non-blocking)
            _furiganaCts?.Cancel();
            _furiganaCts?.Dispose();
            _furiganaCts = new CancellationTokenSource();
            _ = FetchFuriganaAsync(text, _furiganaCts.Token);

            // Fetch translation
            TranslatedText = await GetTranslatedTextAsync(OCRText);
            Console.WriteLine(TranslatedText);

            if (TranslatedText != null)
            {
                _overlayState.Translation = TranslatedText;
                RenderOverlay();
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isSelecting) return;

            System.Windows.Point currentPoint = e.GetPosition(this);
            double left = Math.Min(clickedPoint.X, currentPoint.X);
            double top = Math.Min(clickedPoint.Y, currentPoint.Y);

            selectBorder.Height = Math.Max(clickedPoint.Y, currentPoint.Y) - top;
            selectBorder.Width = Math.Max(clickedPoint.X, currentPoint.X) - left;
            selectBorder.Height += 2;
            selectBorder.Width += 2;

            clippingGeometry.Rect = new Rect(
                new System.Windows.Point(left, top),
                new System.Windows.Size(selectBorder.Width - 2, selectBorder.Height - 2));

            Canvas.SetLeft(selectBorder, left - 1);
            Canvas.SetTop(selectBorder, top - 1);
        }

        private void UpdateTextBlock(string translateText, Rectangle region, double xDimension = 0, double yDimension = 0)
        {
            _overlayState.Translation = translateText;
            _overlayState.Region = region.Width > 0 && region.Height > 0 ? region : null;
            _overlayState.X = xDimension;
            _overlayState.Y = yDimension;
            RenderOverlay();
        }

        /// <summary>
        /// Calculates the optimal font size to fit text within the specified width and height.
        /// </summary>
        /// <param name="text">The text to fit.</param>
        /// <param name="availableWidth">The available width.</param>
        /// <param name="availableHeight">The available height.</param>
        /// <returns>The optimal font size.</returns>
        private double CalculateOptimalFontSize(string text, double availableWidth, double availableHeight)
        {
            if (string.IsNullOrWhiteSpace(text) || availableWidth <= 0 || availableHeight <= 0)
                return 16; // Default font size

            const double maxFontSize = 16; // Maximum font size (same as default)
            const double minFontSize = 8;  // Minimum font size
            const double padding = 20;     // Padding around text

            double effectiveWidth = availableWidth - padding;
            double effectiveHeight = availableHeight - padding;

            // Binary search for the optimal font size
            double low = minFontSize;
            double high = maxFontSize;
            double optimalSize = minFontSize;

            while (low <= high)
            {
                double mid = (low + high) / 2;
                double textHeight = GetTextHeight(text, mid, effectiveWidth);

                if (textHeight <= effectiveHeight)
                {
                    optimalSize = mid;
                    low = mid + 0.5; // Try larger font
                }
                else
                {
                    high = mid - 0.5; // Try smaller font
                }
            }

            return Math.Max(minFontSize, Math.Min(maxFontSize, optimalSize));
        }

        /// <summary>
        /// Gets the height of text with a specific font size and width constraint.
        /// </summary>
        /// <param name="text">The text to measure.</param>
        /// <param name="fontSize">The font size.</param>
        /// <param name="width">The width constraint.</param>
        /// <returns>The text height.</returns>
        private double GetTextHeight(string text, double fontSize, double width)
        {
            var formattedText = new System.Windows.Media.FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface("Segoe UI"),
                fontSize,
                System.Windows.Media.Brushes.Black,
                new NumberSubstitution(),
                VisualTreeHelper.GetDpi(translatedTextBlock).PixelsPerDip);

            formattedText.MaxTextWidth = Math.Max(1, width);
            return formattedText.Height;
        }

        #region Overlay Rendering (Phase 3)

        /// <summary>
        /// Switches between Translation, Furigana, and Original views based on
        /// <see cref="OverlayState.CurrentView"/>, showing the appropriate control
        /// and hiding the other.
        /// </summary>
        private void RenderOverlay()
        {
            if (_overlayState.Region is not { } region)
            {
                // No region captured — hide both controls
                translatedTextBlock.Visibility = Visibility.Collapsed;
                rubyTextBlock.Visibility = Visibility.Collapsed;
                return;
            }

            double x = _overlayState.X;
            double y = _overlayState.Y;

            switch (_overlayState.CurrentView)
            {
                case OverlayViewMode.Furigana:
                    RenderFuriganaView(region, x, y);
                    break;

                case OverlayViewMode.Original:
                    RenderTextView(_overlayState.OcrText, region, x, y);
                    break;

                case OverlayViewMode.Translation:
                default:
                    RenderTextView(_overlayState.Translation, region, x, y);
                    break;
            }
        }

        /// <summary>
        /// Renders the Translation or Original view using <c>translatedTextBlock</c>.
        /// </summary>
        private void RenderTextView(string text, System.Drawing.Rectangle region, double x, double y)
        {
            if (string.IsNullOrEmpty(text))
            {
                translatedTextBlock.Text = "";
                translatedTextBlock.Visibility = Visibility.Collapsed;
                rubyTextBlock.Visibility = Visibility.Collapsed;
                return;
            }

            translatedTextBlock.Text = text;
            translatedTextBlock.Width = region.Width;
            translatedTextBlock.Height = region.Height;

            const double defaultFontSize = 16;
            const double padding = 20;
            double effectiveHeight = region.Height - padding;
            double textHeightWithDefault = GetTextHeight(text, defaultFontSize, region.Width - padding);

            if (textHeightWithDefault <= effectiveHeight)
            {
                translatedTextBlock.FontSize = defaultFontSize;
            }
            else
            {
                translatedTextBlock.FontSize = CalculateOptimalFontSize(text, region.Width, region.Height);
            }

            Canvas.SetLeft(translatedTextBlock, x);
            Canvas.SetTop(translatedTextBlock, y);
            translatedTextBlock.VerticalAlignment = VerticalAlignment.Center;
            translatedTextBlock.Visibility = Visibility.Visible;
            rubyTextBlock.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Renders the Furigana view using <c>rubyTextBlock</c>.
        /// Falls back to Translation view if no furigana data is available.
        /// </summary>
        private void RenderFuriganaView(System.Drawing.Rectangle region, double x, double y)
        {
            if (_overlayState.FuriganaSegments.Count == 0)
            {
                // No furigana data yet — fall back to translation
                _overlayState.CurrentView = OverlayViewMode.Translation;
                RenderTextView(_overlayState.Translation, region, x, y);
                return;
            }

            string surfaceText = string.Concat(_overlayState.FuriganaSegments.Select(s => s.Surface));

            // Calculate optimal font size accounting for ruby row overhead
            double optimalFontSize = CalculateOptimalFontSizeRuby(surfaceText, region.Width, region.Height);

            rubyTextBlock.BaseFontSize = optimalFontSize;
            rubyTextBlock.Width = region.Width;
            rubyTextBlock.Height = region.Height;
            rubyTextBlock.SetSegments(_overlayState.FuriganaSegments);

            Canvas.SetLeft(rubyTextBlock, x);
            Canvas.SetTop(rubyTextBlock, y);
            rubyTextBlock.Visibility = Visibility.Visible;
            translatedTextBlock.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Calculates optimal font size for ruby text, subtracting estimated ruby row overhead
        /// from the available height.
        /// </summary>
        private double CalculateOptimalFontSizeRuby(string text, double availableWidth, double availableHeight)
        {
            if (string.IsNullOrWhiteSpace(text) || availableWidth <= 0 || availableHeight <= 0)
                return 16;

            const double maxFontSize = 16;
            const double minFontSize = 8;
            const double padding = 20;

            double effectiveWidth = availableWidth - padding;
            double effectiveHeight = availableHeight - padding;

            double low = minFontSize;
            double high = maxFontSize;
            double optimalSize = minFontSize;

            while (low <= high)
            {
                double mid = (low + high) / 2;

                // Estimate number of wrap lines for this font size
                // Japanese characters are roughly mid-width each
                double charsPerLine = Math.Max(1, effectiveWidth / mid);
                double estimatedLines = Math.Ceiling(text.Length / charsPerLine);

                // Reserve space for ruby rows (one per line)
                double rubyOverhead = mid * RubyTextBlock.RubyLineOverheadFactor * estimatedLines;
                double adjustedHeight = Math.Max(0, effectiveHeight - rubyOverhead);

                double textHeight = GetTextHeight(text, mid, effectiveWidth);

                if (textHeight <= adjustedHeight)
                {
                    optimalSize = mid;
                    low = mid + 0.5;
                }
                else
                {
                    high = mid - 0.5;
                }
            }

            return Math.Max(minFontSize, Math.Min(maxFontSize, optimalSize));
        }

        /// <summary>
        /// Fetches furigana readings for the given text from the sidecar service.
        /// Runs in the background; updates the overlay if the Furigana view is active.
        /// </summary>
        private async Task FetchFuriganaAsync(string text, CancellationToken ct)
        {
            try
            {
                _furiganaProvider ??= new HttpFuriganaProvider(_settings.Furigana.SidecarUrl);

                var segments = await _furiganaProvider.GetFuriganaAsync(
                    text,
                    _settings.Furigana.UseFlflFallback,
                    ct);

                ct.ThrowIfCancellationRequested();

                _overlayState.FuriganaSegments = segments;

                // If the user has toggled to Furigana view, re-render
                if (_overlayState.CurrentView == OverlayViewMode.Furigana)
                {
                    RenderOverlay();
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when user selects a new region — ignore
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Furigana] Fetch error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles the FuriganaToggleButton Checked event.
        /// Switches the overlay to Furigana view if data is available.
        /// </summary>
        private void FuriganaToggleButton_Checked(object sender, RoutedEventArgs e)
        {
            if (_overlayState.Region == null)
            {
                // No region captured — revert toggle
                FuriganaToggleButton.IsChecked = false;
                return;
            }

            _overlayState.CurrentView = OverlayViewMode.Furigana;
            FuriganaToggleButton.ToolTip = "Switch to Translation View";
            RenderOverlay();
        }

        /// <summary>
        /// Handles the FuriganaToggleButton Unchecked event.
        /// Switches the overlay back to Translation view.
        /// </summary>
        private void FuriganaToggleButton_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_overlayState.Region == null)
                return;

            _overlayState.CurrentView = OverlayViewMode.Translation;
            FuriganaToggleButton.ToolTip = "Toggle Furigana View";
            RenderOverlay();
        }

        #endregion

        private void EditTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            FinishEditing();
        }

        private void TranslatedTextBlock_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && !isEditing)
            {
                isEditing = true;
                // UpdateTextBlock("", new Rectangle(0, 0, 0, 0), 0, 0);

                editTextBox = new TextBox
                {
                    Text = OCRText,
                    Width = translatedTextBlock.Width,
                    Height = translatedTextBlock.Height,
                    TextAlignment = TextAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = translatedTextBlock.FontSize,
                    Background = translatedTextBlock.Background,
                };

                Canvas.SetLeft(editTextBox, Canvas.GetLeft(translatedTextBlock));
                Canvas.SetTop(editTextBox, Canvas.GetTop(translatedTextBlock) - editTextBox.Height - 5);


                vancas.Children.Add(editTextBox);
                editTextBox.Focus();
                editTextBox.SelectAll();
                editTextBox.LostFocus += EditTextBox_LostFocus;
                editTextBox.KeyDown += EditTextBox_KeyDown;
            }
            e.Handled = true;
        }

        private void EditMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!isEditing)
            {
                isEditing = true;

                editTextBox = new TextBox
                {
                    Text = OCRText,
                    Width = translatedTextBlock.ActualHeight, // Use ActualWidth for better sizing
                    Height = translatedTextBlock.ActualWidth, // Use ActualHeight
                    TextAlignment = TextAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = translatedTextBlock.FontSize,
                    Background = translatedTextBlock.Background,
                    TextWrapping = TextWrapping.Wrap, // Ensure wrapping matches
                    AcceptsReturn = true // Allow multi-line editing if needed
                };
                double textBlockLeft = Canvas.GetLeft(translatedTextBlock);
                double textBlockTop = Canvas.GetTop(translatedTextBlock);

                // Determine if the TextBox should be placed above or below the TextBlock
                double availableSpaceAbove = textBlockTop;
                double availableSpaceBelow = vancas.ActualHeight - (textBlockTop + translatedTextBlock.ActualHeight);

                if (availableSpaceAbove > availableSpaceBelow)
                {
                    // Place the TextBox above the TextBlock
                    Canvas.SetLeft(editTextBox, textBlockLeft);
                    Canvas.SetTop(editTextBox, textBlockTop - editTextBox.Height - 5);
                }
                else
                {
                    // Place the TextBox below the TextBlock
                    Canvas.SetLeft(editTextBox, textBlockLeft);
                    Canvas.SetTop(editTextBox, textBlockTop + translatedTextBlock.ActualHeight + 5);
                }


                double editLeft = Canvas.GetLeft(editTextBox);
                double editTop = Canvas.GetTop(editTextBox);

                // Hide the TextBlock and add the TextBox
                translatedTextBlock.Visibility = Visibility.Collapsed;
                vancas.Children.Add(editTextBox);
                editTextBox.Focus();
                editTextBox.SelectAll();
                editTextBox.LostFocus += EditTextBox_LostFocus;
                editTextBox.KeyDown += EditTextBox_KeyDown;
                // Show and position the Finish Edit Button
                FinishEditButton.Visibility = Visibility.Visible;
                // Position button below the textbox
                Canvas.SetLeft(FinishEditButton, editLeft + editTextBox.Width);
                Canvas.SetTop(FinishEditButton, editTop + editTextBox.Height / 3);
            }
        }

        private void FinishEditButton_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("Finished Editing");
            FinishEditing();
        }

        private void EditTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift)
            {
                FinishEditing();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelEditing();
                e.Handled = true;
            }
        }

        private async void FinishEditing()
        {
            if (!isEditing) return;

            if (editTextBox?.Text == null) return;
            OCRText = editTextBox.Text;
            _overlayState.OcrText = OCRText;

            // Kick off furigana fetch for the edited text
            _furiganaCts?.Cancel();
            _furiganaCts?.Dispose();
            _furiganaCts = new CancellationTokenSource();
            _ = FetchFuriganaAsync(OCRText, _furiganaCts.Token);

            TranslatedText = await GetTranslatedTextAsync(editTextBox.Text);
            _overlayState.Translation = TranslatedText;

            translatedTextBlock.Visibility = Visibility.Visible;
            FinishEditButton.Visibility = Visibility.Collapsed;
            vancas.Children.Remove(editTextBox);
            editTextBox = null;
            isEditing = false;

            RenderOverlay();
        }

        private void CancelEditing()
        {
            if (!isEditing) return;

            translatedTextBlock.Visibility = Visibility.Visible;
            vancas.Children.Remove(editTextBox);
            FinishEditButton.Visibility = Visibility.Collapsed;
            editTextBox = null;
            isEditing = false;
        }

        private async void FreezeScreen()
        {
            BackgroundBrush.Opacity = 0;
            await Task.Delay(150);
            SetImageToBackground();
        }

        private void Unfreeze()
        {
            BackgroundBrush.Opacity = 0;
            BG.Source = null;
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            Unfreeze();
            // Clear overlay state and hide both controls
            _overlayState.OcrText = "";
            _overlayState.Translation = "";
            _overlayState.FuriganaSegments.Clear();
            _overlayState.CurrentView = OverlayViewMode.Translation;
            _overlayState.Region = null;
            translatedTextBlock.Text = "";
            translatedTextBlock.Visibility = Visibility.Collapsed;
            rubyTextBlock.Visibility = Visibility.Collapsed;
            selectBorder.BorderThickness = new Thickness(0);
        }

        private void Window_Activated(object sender, EventArgs e)
        {
            FreezeScreen();
            if (!isEditing)
            {
                FinishEditButton.Visibility = Visibility.Collapsed;
            }
        }

        private void Quit()
        {
            if (editTextBox != null)
            {
                editTextBox.LostFocus -= EditTextBox_LostFocus;
                editTextBox.KeyDown -= EditTextBox_KeyDown;
            }
            CursorClipper.UnClipCursor();
            _ocrCts?.Cancel();
            _ocrCts?.Dispose();
            _ocrCts = null;
            _furiganaCts?.Cancel();
            _furiganaCts?.Dispose();
            _furiganaCts = null;
            _furiganaProvider?.Dispose();
            _furiganaProvider = null;
            _furiganaServiceManager.Dispose();
            if (_currentOcrProvider is IDisposable ocrDisposable)
                ocrDisposable.Dispose();
            foreach (var provider in _translationProviders)
            {
                if (provider is IDisposable disposable)
                    disposable.Dispose();
            }
            GC.Collect();
            Application.Current.Shutdown();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            TopButtonStack.Visibility = Visibility.Collapsed;
            CursorClipper.UnClipCursor();
            BG.Source = null;
            _systemTrayIcon?.Dispose();
            _furiganaServiceManager.Dispose();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Maximized;
            FullWindow.Rect = new Rect(0, 0, Width, Height);
            KeyDown += HandleKeyDown;
            SetImageToBackground();
            SearchToggleButton.ToolTip = "Show Dictionary Search";
            // Phase 3: Re-enable furigana toggle for overlay view switching
            FuriganaToggleButton.IsChecked = false;
            FuriganaToggleButton.ToolTip = "Toggle Furigana View";
            FuriganaToggleButton.Checked += FuriganaToggleButton_Checked;
            FuriganaToggleButton.Unchecked += FuriganaToggleButton_Unchecked;

            InitializeTranslationProviderComboBox();
            InitializeModelComboBoxes();

            var displays = DisplayInfo.AllDisplayInfos.ToList();
            if (displays.Count == 0)
            {
                UpdateScreenButton(displays);
                InitializeSystemTrayIcon();
                if (IsMouseOver)
                {
                    TopButtonStack.Visibility = Visibility.Visible;
                }
                return;
            }

            System.Windows.Point mousePos;
            if (!ApplicationUtilities.GetMousePosition(out mousePos))
            {
                mousePos = new System.Windows.Point(0, 0);
            }
            for (int i = 0; i < displays.Count; i++)
            {
                Rect bound = displays[i].ScaledBounds();
                if (bound.Contains(mousePos))
                {
                    currentScreenIndex = i;
                    break;
                }
            }
            UpdateScreenButton(displays);

            InitializeSystemTrayIcon();

            if (IsMouseOver)
            {
                TopButtonStack.Visibility = Visibility.Visible;
            }
        }

        private void InitializeTranslationProviderComboBox()
        {
            TranslationProviderComboBox.Items.Clear();
            foreach (var provider in _translationProviders)
            {
                TranslationProviderComboBox.Items.Add(provider.DisplayName);
            }

            int selectedIndex = _translationProviders.FindIndex(p => p.Name == _settings.TranslationProvider);
            TranslationProviderComboBox.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;

            UpdateTranslationModelVisibility();
        }
        private void UpdateTranslationModelVisibility()
        {
            bool isOllama = _currentTranslationProvider is OllamaTranslationProvider;
            bool isOpenAI = _currentTranslationProvider is OpenAITranslationProvider;
            bool showModels = isOllama || isOpenAI;
            TranslationModelComboBox.Visibility = showModels ? Visibility.Visible : Visibility.Collapsed;
            RefreshTranslationModelsButton.Visibility = showModels ? Visibility.Visible : Visibility.Collapsed;

            if (isOllama)
            {
                LoadOllamaTranslationModels();
            }
            else if (isOpenAI)
            {
                LoadOpenAITranslationModels();
            }
        }

        private async void LoadOpenAITranslationModels()
        {
            if (_currentTranslationProvider is not OpenAITranslationProvider provider) return;

            try
            {
                var models = await provider.ListModelsAsync();
                TranslationModelComboBox.Items.Clear();
                foreach (var model in models)
                {
                    TranslationModelComboBox.Items.Add(model);
                }

                var currentModel = provider.Model;
                if (!string.IsNullOrEmpty(currentModel) && models.Contains(currentModel))
                {
                    TranslationModelComboBox.SelectedItem = currentModel;
                }
                else if (models.Count > 0)
                {
                    TranslationModelComboBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load translation models: {ex.Message}");
                if (_currentTranslationProvider is OpenAITranslationProvider p)
                {
                    TranslationModelComboBox.Items.Add(p.Model);
                    TranslationModelComboBox.SelectedIndex = 0;
                }
            }
        }

        private async void InitializeModelComboBoxes()
        {
            await LoadOllamaOcrModelsAsync();
            UpdateTranslationModelVisibility();
            _initializing = false;
        }

        private async Task LoadOllamaOcrModelsAsync()
        {
            try
            {
                var models = await ProviderFactory.ListModelsForProvider(_settings.OcrProvider, _settings);
                OcrModelComboBox.Items.Clear();
                foreach (var model in models)
                {
                    OcrModelComboBox.Items.Add(model);
                }

                var currentModel = _settings.GetProviderConfig(_settings.OcrProvider).OcrModel;
                if (!string.IsNullOrEmpty(currentModel) && models.Contains(currentModel))
                {
                    OcrModelComboBox.SelectedItem = currentModel;
                }
                else if (models.Count > 0)
                {
                    OcrModelComboBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load OCR models: {ex.Message}");
                var currentModel = _settings.GetProviderConfig(_settings.OcrProvider).OcrModel;
                if (!string.IsNullOrEmpty(currentModel))
                {
                    OcrModelComboBox.Items.Add(currentModel);
                    OcrModelComboBox.SelectedIndex = 0;
                }
            }
        }

        private async void LoadOllamaTranslationModels()
        {
            if (_currentTranslationProvider is not OllamaTranslationProvider ollamaProvider) return;

            try
            {
                var models = await ollamaProvider.ListModelsAsync();
                TranslationModelComboBox.Items.Clear();
                foreach (var model in models)
                {
                    TranslationModelComboBox.Items.Add(model);
                }

                var currentModel = _settings.GetProviderConfig(_currentTranslationProvider.Name).TranslationModel;
                if (!string.IsNullOrEmpty(currentModel) && models.Contains(currentModel))
                {
                    TranslationModelComboBox.SelectedItem = currentModel;
                }
                else if (models.Count > 0)
                {
                    TranslationModelComboBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load translation models: {ex.Message}");
                TranslationModelComboBox.Items.Add(_settings.GetProviderConfig(_currentTranslationProvider.Name).TranslationModel);
                TranslationModelComboBox.SelectedIndex = 0;
            }
        }

        private void InitializeSystemTrayIcon()
        {
            try
            {
                var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/icon.ico"))?.Stream;
                if (iconStream == null)
                {
                    Debug.WriteLine("Warning: icon.ico not found, using default icon");
                    var assemblyLocation = Application.ResourceAssembly?.Location;
                    if (assemblyLocation == null)
                    {
                        throw new InvalidOperationException("Could not get application icon");
                    }
                    using var defaultIcon = System.Drawing.Icon.ExtractAssociatedIcon(assemblyLocation);
                    if (defaultIcon == null)
                    {
                        throw new InvalidOperationException("Could not extract default icon");
                    }
                    _systemTrayIcon = new SystemTrayIcon(defaultIcon, "J2E OCR Translator", RestoreFromTray, OpenSettings, Quit);
                }
                else
                {
                    using var icon = new System.Drawing.Icon(iconStream);
                    _systemTrayIcon = new SystemTrayIcon(icon, "J2E OCR Translator", RestoreFromTray, OpenSettings, Quit);
                }

                _systemTrayIcon.OnIconClicked += (s, e) =>
                {
                    if (WindowState == WindowState.Minimized)
                    {
                        RestoreFromTray();
                    }
                    else
                    {
                        MinimizeWindow();
                    }
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error initializing system tray icon: {ex.Message}");
            }
        }

        private void Window_Unloaded(object sender, RoutedEventArgs e)
        {
            BG.Source = null;
            BG.UpdateLayout();
            CurrentScreen = null;
            StateChanged -= Window_StateChanged;
            Loaded -= Window_Loaded;
            Unloaded -= Window_Unloaded;
            KeyDown -= HandleKeyDown;
            TopButtonStack.Visibility = Visibility.Collapsed;
            CancelButton.Click -= CancelItemClick;
            SettingsButton.Click -= SettingsButton_Click;
            vancas.MouseDown -= Canvas_MouseDown;
            vancas.MouseUp -= Canvas_MouseUp;
            vancas.MouseMove -= Canvas_MouseMove;
            vancas.MouseEnter -= Canvas_MouseEnter;
            vancas.MouseLeave -= Canvas_MouseLeave;
            ScreenSwitchButton.Click -= ScreenSwitchButton_Click;

            SearchToggleButton.Checked -= SearchToggleButton_Checked;
            SearchToggleButton.Unchecked -= SearchToggleButton_Unchecked;
            FuriganaToggleButton.Checked -= FuriganaToggleButton_Checked;
            FuriganaToggleButton.Unchecked -= FuriganaToggleButton_Unchecked;
            CaptureModeToggleButton.Checked -= CaptureModeToggleButton_Checked;
            CaptureModeToggleButton.Unchecked -= CaptureModeToggleButton_Unchecked;
            OcrModelComboBox.SelectionChanged -= OcrModelComboBox_SelectionChanged;
            TranslationProviderComboBox.SelectionChanged -= TranslationProviderComboBox_SelectionChanged;
            TranslationModelComboBox.SelectionChanged -= TranslationModelComboBox_SelectionChanged;
            RefreshOcrModelsButton.Click -= RefreshOcrModelsButton_Click;
            RefreshTranslationModelsButton.Click -= RefreshTranslationModelsButton_Click;
            SearchExecuteButton.Click -= SearchExecuteButton_Click;
            SearchTermTextBox.KeyDown -= SearchTermTextBox_KeyDown;
            SearchTermTextBox.TextChanged -= SearchTermTextBox_TextChanged;
            ClearSearchButton.Click -= ClearSearchButton_Click;

            if (editTextBox != null)
            {
                editTextBox.LostFocus -= EditTextBox_LostFocus;
                editTextBox.KeyDown -= EditTextBox_KeyDown;
            }

            GC.Collect();
        }

        private void SaveSettings()
        {
            try
            {
                _settings.Save();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Settings save error: {ex.Message}");
                MessageBox.Show($"Failed to save settings: {ex.Message}", "Settings Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OcrModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing) return;
            if (OcrModelComboBox.SelectedItem is string model)
            {
                // Update the provider config and recreate provider
                var config = _settings.GetProviderConfig(_settings.OcrProvider);
                config.OcrModel = model;
                SaveSettings();

                // Cancel any in-flight OCR before swapping provider
                _ocrCts?.Cancel();
                _ocrCts?.Dispose();
                _ocrCts = null;
                _furiganaCts?.Cancel();
                _furiganaCts?.Dispose();
                _furiganaCts = null;

                // Dispose old provider and create new one
                if (_currentOcrProvider is IDisposable disposable)
                    disposable.Dispose();
                _currentOcrProvider = ProviderFactory.CreateOcrProvider(_settings);

                Debug.WriteLine($"OCR model changed to: {model}");
            }
        }

        private void RefreshOcrModelsButton_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadOllamaOcrModelsAsync();
        }

        private void TranslationProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing) return;
            int idx = TranslationProviderComboBox.SelectedIndex;
            if (idx >= 0 && idx < _translationProviders.Count)
            {
                _currentTranslationProvider = _translationProviders[idx];
                _settings.TranslationProvider = _currentTranslationProvider.Name;
                SaveSettings();
                Debug.WriteLine($"Translation provider changed to: {_currentTranslationProvider.DisplayName}");
                UpdateTranslationModelVisibility();
            }
        }
        private void TranslationModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing) return;
            if (TranslationModelComboBox.SelectedItem is string model)
            {
                if (_currentTranslationProvider is OllamaTranslationProvider ollamaProvider)
                {
                    ollamaProvider.Model = model;
                    _settings.GetProviderConfig(ollamaProvider.Name).TranslationModel = model;
                }
                else if (_currentTranslationProvider is OpenAITranslationProvider openaiProvider)
                {
                    openaiProvider.Model = model;
                    var config = _settings.GetProviderConfig(openaiProvider.Name);
                    config.TranslationModel = model;
                }
                SaveSettings();
                Debug.WriteLine($"Translation model changed to: {model}");
            }
        }

        private void RefreshTranslationModelsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTranslationProvider is OllamaTranslationProvider)
                LoadOllamaTranslationModels();
            else if (_currentTranslationProvider is OpenAITranslationProvider)
                LoadOpenAITranslationModels();
        }

        private void SearchToggleButton_Checked(object sender, RoutedEventArgs e)
        {
            SearchPanel.Visibility = Visibility.Visible;
            SearchToggleButton.ToolTip = "Hide Dictionary Search";
            SearchTermTextBox.Focus();
        }

        private void SearchToggleButton_Unchecked(object sender, RoutedEventArgs e)
        {
            SearchPanel.Visibility = Visibility.Collapsed;
            SearchToggleButton.ToolTip = "Show Dictionary Search";
        }

        private void CaptureModeToggleButton_Checked(object sender, RoutedEventArgs e)
        {
            captureModeEnabled = true;
            vancas.Cursor = Cursors.Cross;
            CaptureModeToggleButton.ToolTip = "Capture Mode Enabled (Click to disable)";
            BackgroundBrush.Opacity = 0.35;
        }

        private void CaptureModeToggleButton_Unchecked(object sender, RoutedEventArgs e)
        {
            captureModeEnabled = false;
            vancas.Cursor = Cursors.Arrow;
            CaptureModeToggleButton.ToolTip = "Capture Mode Disabled (Click to enable)";
            BackgroundBrush.Opacity = 0.15;
        }

        private void ScreenSwitchButton_Click(object sender, RoutedEventArgs e)
        {
            var displays = DisplayInfo.AllDisplayInfos.ToList();
            if (displays.Count <= 1) return;

            if (currentScreenIndex >= displays.Count)
            {
                currentScreenIndex = 0;
            }
            currentScreenIndex = (currentScreenIndex + 1) % displays.Count;
            MoveToScreen(displays[currentScreenIndex]);
            UpdateScreenButton(displays);
        }

        private void MoveToScreen(DisplayInfo screen)
        {
            Rect bounds = screen.ScaledBounds();
            WindowState = WindowState.Normal;
            Left = bounds.X;
            Top = bounds.Y;
            Width = bounds.Width;
            Height = bounds.Height;
            WindowState = WindowState.Maximized;
            FullWindow.Rect = new Rect(0, 0, bounds.Width, bounds.Height);
            FreezeScreen();
        }

        private void UpdateScreenButton(IReadOnlyList<DisplayInfo> displays)
        {
            if (displays.Count <= 1)
            {
                ScreenSwitchButton.Visibility = Visibility.Collapsed;
                return;
            }
            ScreenSwitchButton.Visibility = Visibility.Visible;
            ScreenNumberText.Text = (currentScreenIndex + 1).ToString();
            var bounds = displays[currentScreenIndex].ScaledBounds();
            ScreenSwitchButton.ToolTip = $"Switch Screen (Monitor {currentScreenIndex + 1}: {(int)bounds.Width}x{(int)bounds.Height})";
        }



        private void SearchExecuteButton_Click(object sender, RoutedEventArgs e)
        {
            PerformSearch();
        }

        private void SearchTermTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                PerformSearch();
                e.Handled = true;
            }
        }

        private void SearchTermTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ClearSearchButton.Visibility = string.IsNullOrWhiteSpace(SearchTermTextBox.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;

            SearchPlaceholder.Visibility = string.IsNullOrWhiteSpace(SearchTermTextBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
        {
            SearchTermTextBox.Clear();
            SearchTermTextBox.Focus();
            ClearSearchButton.Visibility = Visibility.Collapsed;
            SearchPlaceholder.Visibility = Visibility.Visible;
            ResultsCountText.Visibility = Visibility.Collapsed;
            SearchResultsListBox.ItemsSource = null;
        }

        private void PerformSearch()
        {
            string searchTerm = SearchTermTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                Console.WriteLine("Search term is empty or whitespace.");
                return;
            }

            try
            {
                List<JapaneseWord> searchResults = WWWJDict.GetSearchResults(searchTerm);

                if (searchResults.Count == 0)
                {
                    SearchResultsListBox.ItemsSource = new List<JapaneseWord>
                    {
                        new("No results found", "", ["Try searching with different terms"])
                    };
                    ResultsCountText.Text = "0 results found";
                    ResultsCountText.Visibility = Visibility.Visible;
                }
                else
                {
                    SearchResultsListBox.ItemsSource = searchResults;
                    ResultsCountText.Text = $"{searchResults.Count} result{(searchResults.Count > 1 ? "s" : "")} found";
                    ResultsCountText.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                SearchResultsListBox.ItemsSource = new List<JapaneseWord>
                {
                    new("Error", "", [$"Error during search: {ex.Message}"])
                };
                ResultsCountText.Text = "Search failed";
                ResultsCountText.Visibility = Visibility.Visible;
                Console.WriteLine($"Search Error: {ex}");
            }
        }

    }
}