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
            var window = new Window
            {
                Title = "Live Transcriptions",
                Height = 120, // Enough for 2 lines of text
                Width = 800,
                MinHeight = 120, // Min height for two lines of text
                MinWidth = 500, // Min width for subtitle length
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };

            // Position window at bottom of the primary screen using WPF's own API
            window.Left = 100;
            window.Top = SystemParameters.WorkArea.Bottom - 120;

            // Enable resizing in all directions for a borderless window
            var chrome = new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(7), // Increased hit area
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0)
            };
            System.Windows.Shell.WindowChrome.SetWindowChrome(window, chrome);

            // Pin to all virtual desktops after window is shown
            window.SourceInitialized += (s, e) => PinToAllDesktops(window);

            var grid = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) // Nearly transparent (1/255) for hit-testing
            };
            window.Content = grid;

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(180, 20, 20, 20)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20)
            };

            var toolbar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(5)
            };

            var textBlock = new TextBlock
            {
                Text = "Loading...",
                Foreground = Brushes.White,
                FontSize = 18,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
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
                ToolTip = "Settings"
            };

            var closeButton = new Button
            {
                Content = "➜]",
                Width = 30,
                Height = 30,
                Background = Brushes.Transparent,
                Foreground = Brushes.Gray,
                BorderThickness = new Thickness(0),
                FontSize = 18,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Close"
            };

            settingsButton.MouseEnter += (s, e) => { settingsButton.Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)); settingsButton.Foreground = Brushes.White; };
            settingsButton.MouseLeave += (s, e) => { settingsButton.Background = Brushes.Transparent; settingsButton.Foreground = Brushes.Gray; };
            settingsButton.Click += (s, e) => MessageBox.Show("Settings feature coming soon!", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);

            closeButton.MouseEnter += (s, e) => { closeButton.Background = Brushes.Red; closeButton.Foreground = Brushes.White; };
            closeButton.MouseLeave += (s, e) => { closeButton.Background = Brushes.Transparent; closeButton.Foreground = Brushes.Gray; };
            closeButton.Click += (s, e) => Application.Current.Shutdown();

            toolbar.Children.Add(settingsButton);
            toolbar.Children.Add(closeButton);

            var contentGrid = new Grid();
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var eqPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 15, 0),
                Height = 30
            };
            
            var bars = new[]
            {
                new Border { Width = 6, Height = 4, Background = Brushes.LimeGreen, CornerRadius = new CornerRadius(3), Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Bottom },
                new Border { Width = 6, Height = 4, Background = Brushes.Yellow, CornerRadius = new CornerRadius(3), Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Bottom },
                new Border { Width = 6, Height = 4, Background = Brushes.Red, CornerRadius = new CornerRadius(3), Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Bottom }
            };

            foreach (var b in bars) eqPanel.Children.Add(b);

            Grid.SetColumn(eqPanel, 0);
            contentGrid.Children.Add(eqPanel);

            textBlock.TextAlignment = TextAlignment.Left;
            Grid.SetColumn(textBlock, 1);
            contentGrid.Children.Add(textBlock);

            border.Child = contentGrid;
            grid.Children.Add(border);
            grid.Children.Add(toolbar);

            window.MouseLeftButtonDown += (s, e) => {
                if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                    window.DragMove();
            };

            // Initialize transcription after the window is loaded to ensure UI thread is ready
            window.Loaded += async (s, e) =>
            {
                try
                {
                    textBlock.Text = "Downloading Whisper Model...";
                    var modelPath = await ModelDownloader.EnsureModelExists("tiny.en");
                    
                    textBlock.Text = "Initializing GStreamer...";
                    var rnd = new Random();
                    var sentences = new System.Collections.Generic.List<string>();
                    var service = new TranscriptionService(
                        text => {
                            window.Dispatcher.Invoke(() => {
                                // Filter out common hallucinatory empty phrases
                                if (text.StartsWith("[") || text.StartsWith("(") || text.Contains("Thank you.") || text.Trim().Length < 2) return;
                                
                                sentences.Add(text);
                                if (sentences.Count > 3) sentences.RemoveAt(0); // keep max 3 sentences for sliding history
                                textBlock.Text = string.Join(" ", sentences);
                            });
                        },
                        level => {
                            window.Dispatcher.InvokeAsync(() => 
                            {
                                double baseHeight = 4 + (level * 80);
                                if (baseHeight > 30) baseHeight = 30;
                                bars[0].Height = Math.Max(4, baseHeight * (0.5 + rnd.NextDouble() * 0.5));
                                bars[1].Height = Math.Max(4, baseHeight * (0.7 + rnd.NextDouble() * 0.3));
                                bars[2].Height = Math.Max(4, baseHeight * (0.4 + rnd.NextDouble() * 0.6));
                            });
                        }
                    );
                    
                    await service.InitializeAsync(modelPath);
                    textBlock.Text = "Listening...";
                    service.Start();
                }
                catch (Exception ex)
                {
                    var errorMessage = ex.Message;
                    if (ex.InnerException != null)
                    {
                        errorMessage += $"\nInner: {ex.InnerException.Message}";
                    }
                    
                    // Check if model file actually exists and its size
                    var diagnosticModelName = "tiny.en";
                    var modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{diagnosticModelName}.bin");
                    if (File.Exists(modelPath)) {
                        var size = new FileInfo(modelPath).Length;
                        errorMessage += $"\nModel info: {modelPath} ({size} bytes)";
                    } else {
                        errorMessage += $"\nModel info: {modelPath} MISSING";
                    }

                    textBlock.Text = $"Error: {ex.GetType().Name}";
                    MessageBox.Show($"Failed to initialize transcription:\n{errorMessage}\n\nStack: {ex.StackTrace}", "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
