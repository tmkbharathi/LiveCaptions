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
            
            var window = new MainWindow();
            app.Run(window);
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
}
