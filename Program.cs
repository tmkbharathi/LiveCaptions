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
            var app = new Application();

            double screenWidth = SystemParameters.WorkArea.Width;
            double windowWidth = Math.Max(600, screenWidth - 40);
            double windowHeight = 110; // Fits exactly 2 lines of 26px text + padding

            var window = new Window
            {
                Title = "Live Captions",
                Height = windowHeight,
                Width = windowWidth,
                MinHeight = windowHeight,
                MinWidth = 600,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ResizeMode = ResizeMode.CanResize
            };

            // Position at bottom-center of the screen
            window.Left = (SystemParameters.WorkArea.Width - windowWidth) / 2;
            window.Top = SystemParameters.WorkArea.Bottom - windowHeight - 12;

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
                Background = new SolidColorBrush(Color.FromArgb(180, 30, 30, 30)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20, 10, 20, 10)
            };

            // Two-line subtitle display
            var line1Block = new TextBlock
            {
                Text = "",
                Foreground = new SolidColorBrush(Color.FromArgb(180, 210, 210, 210)),
                FontSize = 26,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 2)
            };

            var line2Block = new TextBlock
            {
                Text = "Loading...",
                Foreground = Brushes.White,
                FontSize = 26,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var textPanel = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
            textPanel.Children.Add(line1Block);
            textPanel.Children.Add(line2Block);

            // EQ bars
            var rnd = new Random();
            var eqPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0),
                Height = 36
            };
            var bars = new[]
            {
                new Border { Width = 6, Height = 4, Background = Brushes.LimeGreen, CornerRadius = new CornerRadius(3), Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Bottom },
                new Border { Width = 6, Height = 4, Background = Brushes.Yellow, CornerRadius = new CornerRadius(3), Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Bottom },
                new Border { Width = 6, Height = 4, Background = Brushes.Red, CornerRadius = new CornerRadius(3), Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Bottom }
            };
            foreach (var b in bars) eqPanel.Children.Add(b);

            // Content layout: [EQ] [Text]
            var contentGrid = new Grid();
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(eqPanel, 0);
            Grid.SetColumn(textPanel, 1);
            contentGrid.Children.Add(eqPanel);
            contentGrid.Children.Add(textPanel);

            background.Child = contentGrid;
            rootGrid.Children.Add(background);

            // Settings ⚙ and Close ✕ buttons — top right
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 8, 10, 0)
            };

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
            settingsButton.Click += (s, e) => MessageBox.Show("Settings coming soon!", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);

            closeButton.MouseEnter += (s, e) => { closeButton.Background = Brushes.Red; closeButton.Foreground = Brushes.White; };
            closeButton.MouseLeave += (s, e) => { closeButton.Background = Brushes.Transparent; closeButton.Foreground = Brushes.Gray; };
            closeButton.Click += (s, e) => Application.Current.Shutdown();

            buttonPanel.Children.Add(settingsButton);
            buttonPanel.Children.Add(closeButton);
            rootGrid.Children.Add(buttonPanel);

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
                    line2Block.Text = "Downloading Whisper Model...";
                    var modelPath = await ModelDownloader.EnsureModelExists("tiny.en");
                    line1Block.Text = "";
                    line2Block.Text = "Initializing...";

                    // Output manager owns all subtitle display logic
                    var outputManager = new Output.SubtitleOutputManager(
                        text => line1Block.Text = text,
                        text => line2Block.Text = text
                    );

                    var service = new TranscriptionService(
                        (text, isFinal) =>
                        {
                            window.Dispatcher.Invoke(() => outputManager.OnText(text, isFinal));
                        },
                        level =>
                        {
                            window.Dispatcher.InvokeAsync(() =>
                            {
                                double bh = 4 + (level * 80);
                                if (bh > 36) bh = 36;
                                bars[0].Height = Math.Max(4, bh * (0.5 + rnd.NextDouble() * 0.5));
                                bars[1].Height = Math.Max(4, bh * (0.7 + rnd.NextDouble() * 0.3));
                                bars[2].Height = Math.Max(4, bh * (0.4 + rnd.NextDouble() * 0.6));
                            });
                        }
                    );

                    await service.InitializeAsync(modelPath);
                    line1Block.Text = "Listening...";
                    line2Block.Text = "";
                    service.Start();
                }
                catch (Exception ex)
                {
                    var errorMessage = ex.Message;
                    if (ex.InnerException != null)
                        errorMessage += $"\nInner: {ex.InnerException.Message}";

                    var diagnosticModelName = "tiny.en";
                    var modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{diagnosticModelName}.bin");
                    errorMessage += File.Exists(modelPath)
                        ? $"\nModel: {modelPath} ({new FileInfo(modelPath).Length} bytes)"
                        : $"\nModel: {modelPath} MISSING";

                    line2Block.Text = $"Error: {ex.GetType().Name}";
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
