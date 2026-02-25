namespace LiveTranscriptionApp
{
    using System;
    using System.IO;
    using System.Text.Json;

    public static class AppSettingsManager
    {
        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LiveCaptions",
            "settings.json"
        );

        public static void Save()
        {
            try
            {
                var directory = Path.GetDirectoryName(SettingsFilePath);
                if (directory != null && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                var settings = new
                {
                    Preferences.IncludeMicrophone,
                    Preferences.FilterProfanity,
                    Preferences.ShowAudioTags,
                    Preferences.CurrentStyle,
                    Preferences.CurrentPosition,
                    Preferences.SavedWidth,
                    Preferences.SavedHeight,
                    Preferences.SavedX,
                    Preferences.SavedY
                };

                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Could not save settings: " + ex.Message);
            }
        }

        public static void Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    JsonElement root = doc.RootElement;

                    if (root.TryGetProperty(nameof(Preferences.IncludeMicrophone), out var el)) Preferences.IncludeMicrophone = el.GetBoolean();
                    if (root.TryGetProperty(nameof(Preferences.FilterProfanity), out el)) Preferences.FilterProfanity = el.GetBoolean();
                    if (root.TryGetProperty(nameof(Preferences.ShowAudioTags), out el)) Preferences.ShowAudioTags = el.GetBoolean();
                    if (root.TryGetProperty(nameof(Preferences.CurrentStyle), out el)) Preferences.CurrentStyle = (CaptionStyle)el.GetInt32();
                    if (root.TryGetProperty(nameof(Preferences.CurrentPosition), out el)) Preferences.CurrentPosition = (WindowPosition)el.GetInt32();
                    if (root.TryGetProperty(nameof(Preferences.SavedWidth), out el)) Preferences.SavedWidth = el.GetDouble();
                    if (root.TryGetProperty(nameof(Preferences.SavedHeight), out el)) Preferences.SavedHeight = el.GetDouble();
                    if (root.TryGetProperty(nameof(Preferences.SavedX), out el)) Preferences.SavedX = el.GetDouble();
                    if (root.TryGetProperty(nameof(Preferences.SavedY), out el)) Preferences.SavedY = el.GetDouble();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Could not load settings: " + ex.Message);
            }
        }
    }
}
