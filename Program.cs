using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Threading.Tasks;
using System.IO;

namespace LiveTranscriptionApp
{
    public class Program
    {
        [STAThread]
        public static void Main()
        {
            Console.WriteLine("Application starting...");
            try 
            {
                SetupGStreamerPath();
                Console.WriteLine("GStreamer path configured.");
                
                // Call a separate method to prevent JITing GStreamer-dependent code 
                // until after we expect the environment to be ready.
                RunApp();
            }
            catch (Exception ex)
            {
                var log = $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
                Console.WriteLine($"FATAL CRASH: {log}");
                File.WriteAllText("crash_log.txt", log);
                MessageBox.Show($"FATAL CRASH:\n{ex.Message}\n\nStack trace saved to crash_log.txt", "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void RunApp()
        {
            AppSettingsManager.Load();
            var app = new Application();

            double screenWidth = SystemParameters.WorkArea.Width;
            double windowWidth = Preferences.SavedWidth > 0 ? Preferences.SavedWidth : Math.Max(600, screenWidth - 40);
            double windowHeight = Preferences.SavedHeight > 0 ? Preferences.SavedHeight : 70; // Fits exactly 2 lines of 26px text + padding

            var window = new Window
            {
                Title = "Live Captions",
                Height = windowHeight,
                Width = windowWidth,
                MinHeight = 80,
                MinWidth = 540,
                MaxWidth = 800,
                MaxHeight = 300,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ResizeMode = ResizeMode.CanResize
            };

            // Restore position or default to bottom-center
            if (Preferences.SavedX >= 0 && Preferences.SavedY >= 0)
            {
                window.Left = Preferences.SavedX;
                window.Top = Preferences.SavedY;
            }
            else
            {
                window.Left = (screenWidth - windowWidth) / 2;
                window.Top = SystemParameters.WorkArea.Bottom - windowHeight - 12;
            }

            // Save geometry on move or resize
            window.LocationChanged += (s, e) => {
                Preferences.SavedX = window.Left;
                Preferences.SavedY = window.Top;
                AppSettingsManager.Save();
            };
            window.SizeChanged += (s, e) => {
                Preferences.SavedWidth = window.ActualWidth;
                Preferences.SavedHeight = window.ActualHeight;
                AppSettingsManager.Save();
            };

            // Prevent maximizing
            window.StateChanged += (s, e) => {
                if (window.WindowState == WindowState.Maximized)
                    window.WindowState = WindowState.Normal;
            };

            // Borderless window chrome
            var chrome = new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(5),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0)
            };
            System.Windows.Shell.WindowChrome.SetWindowChrome(window, chrome);

            window.SourceInitialized += (s, e) => PinToAllDesktops(window);

            // Root grid (transparent, for hit-testing)
            var rootGrid = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0))
            };
            window.Content = rootGrid;

            // Dark semi-transparent overlay — with border
            var background = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 30, 30, 30)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20, 10, 20, 10)
            };

            // Ten-line subtitle display (dynamic)
            var lineBlocks = new TextBlock[10];
            var textPanel = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
            
            for (int i = 0; i < 10; i++)
            {
                lineBlocks[i] = new TextBlock
                {
                    Text = " ", // Seed with space to prevent horizontal collapsing
                    Foreground = Brushes.White,
                    FontSize = 20,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 0, 0, 2),
                    MinHeight = 26, // Prevent vertical jumping when empty
                    Visibility = i < 2 ? Visibility.Visible : Visibility.Collapsed // Default to showing 2 lines unless expanded
                };
                
                // First line default style
                if (i == 0) lineBlocks[i].Foreground = new SolidColorBrush(Color.FromArgb(180, 210, 210, 210));
                
                textPanel.Children.Add(lineBlocks[i]);
            }

            // Only the final block has loading text initially
            lineBlocks[1].Text = "Loading...";

            // Shared Application State
            TranscriptionService? activeService = null;
            Output.SubtitleOutputManager? outputManager = null;
            
            Func<int> calculateChars = () => 
            {
                double scaleFactor = lineBlocks[0].FontSize / 20.0;
                // Subtract 100px to account for window padding (20px each side) + button column (~60px)
                // Divisor 9.5 ensures better width utilization
                return (int)(((window.ActualWidth - 100) / 9.5) / scaleFactor);
            };

            // Audio Indicator Dot
            var audioIndicator = new Border
            {
                Width = 8,
                Height = 8,
                Background = new SolidColorBrush(Color.FromRgb(204, 85, 0)), // Dark orange
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 10, 10),
                Opacity = 0 // Hidden by default until audio is detected
            };

            // Layout structure: 2 columns (Captions on Left, Buttons on Right)
            var layoutGrid = new Grid();
            layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Col 0: Captions
            layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Col 1: Tools

            // Content layout: [Text]
            var contentGrid = new Grid();
            contentGrid.Children.Add(textPanel);
            
            Grid.SetColumn(contentGrid, 0);
            layoutGrid.Children.Add(contentGrid);

            background.Child = layoutGrid;
            rootGrid.Children.Add(background);
            rootGrid.Children.Add(audioIndicator);

            // Settings ⚙ and Close ✕ buttons — placed in right column
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(5, 0, 0, 0) // Spacing from text
            };
            Grid.SetColumn(buttonPanel, 1);
            layoutGrid.Children.Add(buttonPanel);

            var settingsButton = new Button
            {
                Content = "🌣",
                Width = 30,
                Height = 30,
                Background = Brushes.Transparent,
                Foreground = Brushes.Gray,
                BorderThickness = new Thickness(0),
                FontSize = 18,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Settings",
                Margin = new Thickness(0, 0, 4, 0)
            };

            var closeButton = new Button
            {
                Content = "✕",
                Width = 30,
                Height = 30,
                Background = Brushes.Transparent,
                Foreground = Brushes.Gray,
                BorderThickness = new Thickness(0),
                FontSize = 16,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Close"
            };

            settingsButton.MouseEnter += (s, e) => { settingsButton.Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)); settingsButton.Foreground = Brushes.White; };
            settingsButton.MouseLeave += (s, e) => { settingsButton.Background = Brushes.Transparent; settingsButton.Foreground = Brushes.Gray; };
            
            // Create inline Settings Dropdown
            var settingsPopup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = settingsButton,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true
            };

            var popupBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(240, 30, 30, 30)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(15),
                Margin = new Thickness(0, 5, 10, 0)
            };

            var settingsStack = new StackPanel();

            var micCheck = new CheckBox 
            { 
                Content = "Include Microphone Audio", 
                Foreground = Brushes.White,
                IsChecked = Preferences.IncludeMicrophone,
                Margin = new Thickness(0, 0, 0, 10)
            };
            micCheck.Checked += (s, e) => Preferences.IncludeMicrophone = true;
            micCheck.Unchecked += (s, e) => Preferences.IncludeMicrophone = false;

            var profanityCheck = new CheckBox 
            { 
                Content = "Filter Profanity", 
                Foreground = Brushes.White,
                IsChecked = Preferences.FilterProfanity,
                Margin = new Thickness(0, 0, 0, 10)
            };
            profanityCheck.Checked += (s, e) => Preferences.FilterProfanity = true;
            profanityCheck.Unchecked += (s, e) => Preferences.FilterProfanity = false;

            var audioTagsCheck = new CheckBox 
            { 
                Content = "Show Audio Tags (e.g. [music])", 
                Foreground = Brushes.White,
                IsChecked = Preferences.ShowAudioTags,
                Margin = new Thickness(0, 0, 0, 15)
            };
            audioTagsCheck.Checked += (s, e) => Preferences.ShowAudioTags = true;
            audioTagsCheck.Unchecked += (s, e) => Preferences.ShowAudioTags = false;

            var styleLabel = new TextBlock 
            { 
                Text = "Caption Style", 
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 5) 
            };
            var styleCombo = new ComboBox { Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
            styleCombo.Items.Add("Default");
            styleCombo.Items.Add("White on Black");
            styleCombo.Items.Add("Small Caps");
            styleCombo.Items.Add("Large Text");
            styleCombo.Items.Add("Yellow on Blue");
            styleCombo.SelectedIndex = (int)Preferences.CurrentStyle;

            settingsStack.Children.Add(micCheck);
            settingsStack.Children.Add(profanityCheck);
            settingsStack.Children.Add(audioTagsCheck);

            var posLabel = new TextBlock { Text = "Window Position", Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 5) };
            var posCombo = new ComboBox { Width = 180, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 15) };
            posCombo.Items.Add("Bottom Center");
            posCombo.Items.Add("Top Center");
            posCombo.Items.Add("Left Center");
            posCombo.Items.Add("Right Center");
            posCombo.Items.Add("Top Left");
            posCombo.Items.Add("Top Right");
            posCombo.Items.Add("Bottom Left");
            posCombo.Items.Add("Bottom Right");
            posCombo.SelectedIndex = (int)Preferences.CurrentPosition;
            
            settingsStack.Children.Add(posLabel);
            settingsStack.Children.Add(posCombo);
            settingsStack.Children.Add(styleLabel);
            settingsStack.Children.Add(styleCombo);
            popupBorder.Child = settingsStack;
            settingsPopup.Child = popupBorder;

            Action applyPosition = () =>
            {
                double sw = SystemParameters.WorkArea.Width;
                double sh = SystemParameters.WorkArea.Height;
                double ww = window.ActualWidth > 0 ? window.ActualWidth : Math.Max(600, sw - 40);
                double wh = window.ActualHeight > 0 ? window.ActualHeight : 70;

                switch (Preferences.CurrentPosition)
                {
                    case WindowPosition.Bottom:
                        window.Left = (sw - ww) / 2;
                        window.Top = sh - wh - 20;
                        break;
                    case WindowPosition.Top:
                        window.Left = (sw - ww) / 2;
                        window.Top = 20;
                        break;
                    case WindowPosition.Left:
                        window.Left = 20;
                        window.Top = (sh - wh) / 2;
                        break;
                    case WindowPosition.Right:
                        window.Left = sw - ww - 20;
                        window.Top = (sh - wh) / 2;
                        break;
                    case WindowPosition.TopLeft:
                        window.Left = 20;
                        window.Top = 20;
                        break;
                    case WindowPosition.TopRight:
                        window.Left = sw - ww - 20;
                        window.Top = 20;
                        break;
                    case WindowPosition.BottomLeft:
                        window.Left = 20;
                        window.Top = sh - wh - 20;
                        break;
                    case WindowPosition.BottomRight:
                        window.Left = sw - ww - 20;
                        window.Top = sh - wh - 20;
                        break;
                }
            };

            posCombo.SelectionChanged += (s, e) => 
            {
                Preferences.CurrentPosition = (WindowPosition)posCombo.SelectedIndex;
                applyPosition();
            };

            // Dynamic theme engine mapping
            Action applyStyles = () =>
            {
                // Reset to Default Style
                background.Background = new SolidColorBrush(Color.FromArgb(255, 30, 30, 30));
                background.BorderBrush = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255));
                for (int i = 0; i < 10; i++)
                {
                    lineBlocks[i].Foreground = Brushes.White;
                    lineBlocks[i].FontSize = 20;
                    lineBlocks[i].MinHeight = lineBlocks[i].FontSize * 1.7; // Extra clearance for descenders (g, y)
                    System.Windows.Documents.Typography.SetCapitals(lineBlocks[i], FontCapitals.Normal);
                }
                lineBlocks[0].Foreground = new SolidColorBrush(Color.FromArgb(180, 210, 210, 210));

                switch (Preferences.CurrentStyle)
                {
                    case CaptionStyle.WhiteOnBlack:
                        background.Background = new SolidColorBrush(Color.FromArgb(240, 0, 0, 0)); // Very dark/pure black
                        lineBlocks[0].Foreground = Brushes.White;
                        break;
                    case CaptionStyle.SmallCaps:
                        for (int i = 0; i < 10; i++) System.Windows.Documents.Typography.SetCapitals(lineBlocks[i], FontCapitals.SmallCaps);
                        break;
                    case CaptionStyle.LargeText:
                        for (int i = 0; i < 10; i++) 
                        {
                            lineBlocks[i].FontSize = 38;
                            lineBlocks[i].MinHeight = lineBlocks[i].FontSize * 1.7;
                        }
                        break;
                    case CaptionStyle.YellowOnBlue:
                        background.Background = new SolidColorBrush(Color.FromArgb(200, 0, 51, 153)); // Dark Blue
                        background.BorderBrush = new SolidColorBrush(Color.FromArgb(150, 0, 102, 255));
                        for (int i = 0; i < 10; i++) lineBlocks[i].Foreground = Brushes.Yellow;
                        lineBlocks[0].Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 0)); // Pale Yellow
                        break;
                }

                // Recalculate wrapping instantly anytime font size changes
                // Show at least 2 lines + 45px padding (safety buffer for descenders)
                window.MinHeight = (lineBlocks[0].MinHeight * 2) + 45 + (lineBlocks[0].Margin.Bottom * 2);

                if (outputManager != null)
                {
                    outputManager.CharsPerLine = calculateChars();
                    double rowHeight = lineBlocks[0].MinHeight + lineBlocks[0].Margin.Bottom;
                    // ActualHeight - 30 accounts for window vertical padding + safety buffer.
                    outputManager.VisibleLines = Math.Max(2, Math.Min(10, (int)((window.ActualHeight - 30) / rowHeight)));
                }
            };

            styleCombo.SelectionChanged += (s, e) => 
            {
                Preferences.CurrentStyle = (CaptionStyle)styleCombo.SelectedIndex;
                applyStyles();
            };

            // Toggle Popup onClick
            settingsButton.Click += (s, e) => 
            {
                settingsPopup.IsOpen = true;
            };


            closeButton.MouseEnter += (s, e) => { closeButton.Background = Brushes.Red; closeButton.Foreground = Brushes.White; };
            closeButton.MouseLeave += (s, e) => { closeButton.Background = Brushes.Transparent; closeButton.Foreground = Brushes.Gray; };
            closeButton.Click += async (s, e) => 
            {
                if (activeService != null)
                {
                    lineBlocks[0].Text = "Shutting down safely...";
                    lineBlocks[1].Text = "";
                    await activeService.DisposeAsync();
                }
                Application.Current.Shutdown();
            };

            buttonPanel.Children.Add(settingsButton);
            buttonPanel.Children.Add(closeButton);
            // buttonPanel is already added to layoutGrid Row 0

            // Drag to move
            window.MouseLeftButtonDown += (s, e) => {
                if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                    window.DragMove();
            };

            // Initialize transcription after window is loaded
            window.Loaded += async (s, e) =>
            {
                try
                {
                    lineBlocks[1].Text = "Downloading Whisper Model...";
                    var modelPath = await ModelDownloader.EnsureModelExists("tiny");
                    lineBlocks[0].Text = "";
                    lineBlocks[1].Text = "Initializing...";

                    // Generate array of dispatcher actions to feed to the 10-line SubtitleManager
                    var dispatchers = new Action<string>[10];
                    for (int i = 0; i < 10; i++)
                    {
                        int index = i; // capture loop variable
                        dispatchers[index] = text => lineBlocks[index].Text = text;
                    }

                    outputManager = new Output.SubtitleOutputManager(dispatchers);
                    outputManager.CharsPerLine = calculateChars();
                    double initialRowHeight = lineBlocks[0].MinHeight + lineBlocks[0].Margin.Bottom;
                    // ActualHeight - 30 accounts for window vertical padding + safety buffer.
                    outputManager.VisibleLines = Math.Max(2, Math.Min(10, (int)((window.ActualHeight - 30) / initialRowHeight)));

                    // Dynamically recalculate char limit when the user resizes the window OR changes the font size
                    window.SizeChanged += (sender, args) => {
                        if (outputManager == null) return;
                        outputManager.CharsPerLine = calculateChars();
                        // Dynamically reveal TextBlocks if window height increases to fit them
                        double rowHeight = lineBlocks[0].MinHeight + lineBlocks[0].Margin.Bottom;
                        int maxVisibleLines = Math.Max(2, Math.Min(10, (int)((window.ActualHeight - 30) / rowHeight)));
                        outputManager.VisibleLines = maxVisibleLines;
                        
                        for (int i = 0; i < 10; i++)
                        {
                            lineBlocks[i].Visibility = i < maxVisibleLines ? Visibility.Visible : Visibility.Collapsed;
                        }
                    };
                    settingsButton.Click += (sender, args) => {
                        // After settings close, ensure the character wrapping map recalculates for Large Text
                        outputManager.CharsPerLine = calculateChars();
                    };

                    var service = new TranscriptionService(
                        (text, isFinal) =>
                        {
                            window.Dispatcher.Invoke(() => outputManager.OnText(text, isFinal));
                        },
                        level =>
                        {
                            window.Dispatcher.InvokeAsync(() =>
                            {
                                // Show dark orange dot if any audio waveform is detected above baseline
                                audioIndicator.Opacity = level > 0.01 ? 1.0 : 0.0;
                            });
                        }
                    );

                    activeService = service;

                    await service.InitializeAsync(modelPath);
                    lineBlocks[0].Text = "Listening...";
                    lineBlocks[1].Text = "";
                    service.Start();
                }
                catch (Exception ex)
                {
                    var errorMessage = ex.Message;
                    if (ex.InnerException != null)
                        errorMessage += $"\nInner: {ex.InnerException.Message}";

                    var diagnosticModelName = "tiny";
                    var modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{diagnosticModelName}.bin");
                    errorMessage += File.Exists(modelPath)
                        ? $"\nModel: {modelPath} ({new FileInfo(modelPath).Length} bytes)"
                        : $"\nModel: {modelPath} MISSING";

                    lineBlocks[1].Text = $"Error: {ex.GetType().Name}";
                    MessageBox.Show($"Failed to initialize:\n{errorMessage}\n\nStack: {ex.StackTrace}", "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            app.Run(window);
        }

        private static void PinToAllDesktops(Window window)
        {
            try 
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                
                // Add WS_EX_TOOLWINDOW to prevent showing in Alt+Tab and help with overlay behavior
                int exStyle = GetWindowLong(hwnd, -20); // GWL_EXSTYLE
                SetWindowLong(hwnd, -20, exStyle | 0x00000080); // WS_EX_TOOLWINDOW

                // Set the window to show on all virtual desktops via the DWMWA attribute
                var manager = (IVirtualDesktopManager)new CVirtualDesktopManager();
                var desktopId = Guid.Empty; // Empty = pin to all desktops (undocumented but works)
                // Pinning via DwmSetWindowAttribute for SHOW_ALWAYS_ON_VD
                // 0x28 = DWMWA_EXTENDED_FRAME_BOUNDS_ON_ALL_DESKTOPS (or use VirtualDesktopPinning)
                SetPropA(hwnd, "VirtualDesktopPinningMode", (IntPtr)1);
                PinWindow(hwnd);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not pin to all desktops: {ex.Message}");
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetPropA(IntPtr hWnd, string lpString, IntPtr hData);

        // Use VirtualDesktop COM registry trick to pin the window
        private static void PinWindow(IntPtr hwnd)
        {
            try 
            {
                // CLSID_VirtualDesktopPinnedApps = b75bcbdb-9010-4eb7-8c04-a5df12cfc2fb
                // We use the simpler approach: IVirtualDesktopManager MoveWindow to all desktops
                // For Windows 10/11, GetWindowDesktopId + PinApp does the job via shell COM
                var manager = (IVirtualDesktopManager)new CVirtualDesktopManager();
                // Trick: move to Guid.Empty which is interpreted as "all desktops" on Win11
                // On Win10 we just make it topmost - it's already done
                manager.MoveWindowToDesktop(hwnd, Guid.Empty);
            }
            catch
            {
                // Silently ignore - different Windows versions have different implementations
            }
        }

        private static void SetupGStreamerPath()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var pathsToTry = new[] {
                Path.Combine(baseDir, "gstreamer", "win-x64", "bin"),
                @"C:\Program Files\gstreamer\1.0\msvc_x86_64\bin",
                @"C:\gstreamer\1.0\msvc_x86_64\bin"
            };

            foreach (var gstBinPath in pathsToTry)
            {
                if (Directory.Exists(gstBinPath))
                {
                    var existingPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                    if (!existingPath.Contains(gstBinPath))
                    {
                        Environment.SetEnvironmentVariable("PATH", $"{gstBinPath};{existingPath}");
                    }

                    // Set GST_PLUGIN_PATH
                    var gstRoot = Path.GetDirectoryName(gstBinPath);
                    if (gstRoot != null)
                    {
                        var pluginPath = Path.Combine(gstRoot, "lib", "gstreamer-1.0");
                        if (Directory.Exists(pluginPath))
                        {
                            Environment.SetEnvironmentVariable("GST_PLUGIN_PATH_1_0", pluginPath);
                            Environment.SetEnvironmentVariable("GST_PLUGIN_PATH", pluginPath);
                        }
                    }
                    break;
                }
            }
        }
    }

    // --- COM Interop for Virtual Desktop Pinning ---
    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopManager
    {
        bool IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow);
        Guid GetWindowDesktopId(IntPtr topLevelWindow);
        void MoveWindowToDesktop(IntPtr topLevelWindow, in Guid desktopId);
    }

    [ComImport, Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
    class CVirtualDesktopManager { }
}
