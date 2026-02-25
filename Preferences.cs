namespace LiveTranscriptionApp
{
    public static class Preferences
    {
        public static bool IncludeMicrophone { get; set; } = false;
        public static bool FilterProfanity { get; set; } = false;
        public static bool ShowAudioTags { get; set; } = false;
        public static CaptionStyle CurrentStyle { get; set; } = CaptionStyle.WhiteOnBlack;
        public static WindowPosition CurrentPosition { get; set; } = WindowPosition.Bottom;

        
        // Window geometry
        public static double SavedWidth { get; set; } = -1;
        public static double SavedHeight { get; set; } = -1;
        public static double SavedX { get; set; } = -1;
        public static double SavedY { get; set; } = -1;
    }

    public enum WindowPosition
    {
        Bottom,
        Top,
        Left,
        Right,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    public enum CaptionStyle
    {
        Default,
        WhiteOnBlack,
        SmallCaps,
        LargeText,
        YellowOnBlue
    }
}
