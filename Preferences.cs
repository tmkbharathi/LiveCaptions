namespace LiveTranscriptionApp
{
    public static class Preferences
    {
        public static bool IncludeMicrophone { get; set; } = false;
        public static bool FilterProfanity { get; set; } = false;
        public static CaptionStyle CurrentStyle { get; set; } = CaptionStyle.WhiteOnBlack;
        public static WindowPosition CurrentPosition { get; set; } = WindowPosition.Bottom;
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
