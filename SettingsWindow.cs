using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LiveTranscriptionApp
{
    public class SettingsWindow : Window
    {
        public event Action? SettingsChanged;

        public SettingsWindow()
        {
            Title = "Live Captions Settings";
            Width = 350;
            Height = 250;
            WindowStyle = WindowStyle.ToolWindow;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromArgb(255, 30, 30, 30));
            Foreground = Brushes.White;
            Topmost = true;

            var stack = new StackPanel { Margin = new Thickness(20) };

            // 1. Microphone Checkbox
            var micCheck = new CheckBox 
            { 
                Content = "Include Microphone Audio", 
                Foreground = Brushes.White,
                IsChecked = Preferences.IncludeMicrophone,
                Margin = new Thickness(0, 0, 0, 15)
            };
            micCheck.Checked += (s, e) => { Preferences.IncludeMicrophone = true; SettingsChanged?.Invoke(); };
            micCheck.Unchecked += (s, e) => { Preferences.IncludeMicrophone = false; SettingsChanged?.Invoke(); };

            // 2. Profanity Checkbox
            var profanityCheck = new CheckBox 
            { 
                Content = "Filter Profanity", 
                Foreground = Brushes.White,
                IsChecked = Preferences.FilterProfanity,
                Margin = new Thickness(0, 0, 0, 25)
            };
            profanityCheck.Checked += (s, e) => { Preferences.FilterProfanity = true; SettingsChanged?.Invoke(); };
            profanityCheck.Unchecked += (s, e) => { Preferences.FilterProfanity = false; SettingsChanged?.Invoke(); };

            // 3. Caption Style Dropdown
            var styleLabel = new TextBlock 
            { 
                Text = "Caption Style", 
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 5) 
            };
            var styleCombo = new ComboBox 
            {
                Width = 200,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            styleCombo.Items.Add("White on Black (Default)");
            styleCombo.Items.Add("Small Caps");
            styleCombo.Items.Add("Large Text");
            styleCombo.Items.Add("Yellow on Blue");
            
            styleCombo.SelectedIndex = (int)Preferences.CurrentStyle;
            styleCombo.SelectionChanged += (s, e) => 
            {
                Preferences.CurrentStyle = (CaptionStyle)styleCombo.SelectedIndex;
                SettingsChanged?.Invoke();
            };

            var btnClose = new Button 
            {
                Content = "Done",
                Width = 100,
                Height = 30,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0),
                Background = new SolidColorBrush(Color.FromArgb(255, 60, 60, 60)),
                Foreground = Brushes.White
            };
            btnClose.Click += (s, e) => Close();

            stack.Children.Add(micCheck);
            stack.Children.Add(profanityCheck);
            stack.Children.Add(styleLabel);
            stack.Children.Add(styleCombo);
            stack.Children.Add(btnClose);

            Content = stack;
        }
    }
}
