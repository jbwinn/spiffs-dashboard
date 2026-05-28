namespace SalesLedger.Core.Theme
{
    /// <summary>
    /// Unified Design System Color Tokens.
    /// Exposes color constants that are shared by the Avalonia Desktop UI (via dynamic resources)
    /// and the QuestPDF generator (for high-contrast printed reports).
    /// </summary>
    public static class AppTheme
    {
        // Brand Typography Fonts
        public const string FontPrimaryName = "ITC Avant Garde Gothic Pro, Segoe UI, Roboto, Helvetica Neue, sans-serif";
        public const string FontSecondaryName = "Warbler Deck, Georgia, Times New Roman, serif";

        // Font Family Singles (For PDF report rendering)
        public const string PrintFontPrimary = "ITC Avant Garde Gothic Pro";
        public const string PrintFontSecondary = "Warbler Deck";

        // Font Families with fallbacks (For PDF report rendering)
        public static readonly string[] PrintFontPrimaryFamilies = new[] { "ITC Avant Garde Gothic Pro", "Segoe UI", "Arial", "sans-serif" };
        public static readonly string[] PrintFontSecondaryFamilies = new[] { "Warbler Deck", "Georgia", "Times New Roman", "serif" };

        // Desktop Dark Theme Colors (Avalonia UI)
        public const string BgApp = "#1A1817";          // Dark warm grey (derived from CS-Grey #332F2E)
        public const string BgCard = "#2A2726";         // Darker warm card grey
        public const string BgOverlay = "#D0121111";    // Semi-transparent overlay backdrop
        public const string Border = "#3D3837";         // Border warm grey
        public const string TextPrimary = "#F7F7FF";    // CS-White
        public const string TextSecondary = "#ADACAB";  // Grays Step 3 (medium contrast gray)
        public const string TextMuted = "#5C5958";      // Grays Step 1 (low contrast gray)

        // Brand Accents
        public const string AccentBlue = "#4F759B";     // CS-Blue
        public const string AccentBlueHover = "#7291AF";// Blues Step 1
        public const string AccentGreen = "#A5CC6B";    // CS-Green
        public const string AccentGreenHover = "#B7D689";// Greens Step 1
        public const string AccentYellow = "#F5C44C";   // CS-Yellow (main warning/commission highlight)
        public const string AccentYellowHover = "#F7D070";// Yellows Step 1
        public const string AccentBrown = "#5D4532";    // CS-Brown
        public const string AccentBrownHover = "#7D6A5B";// Browns Step 1
        public const string AccentRed = "#E05A5D";      // Warm coral red for errors
        public const string AccentRedHover = "#F87171";  // Coral red hover
        public const string AccentPurple = "#705E78";   // Brand-matching Slate Plum (complements CS-Blue/CS-Brown)
        public const string AccentPurpleHover = "#594960";// Darker Slate Plum hover

        // Print Theme Colors (QuestPDF Payout Reports)
        // High-contrast colors configured for printer efficiency and readability
        public const string PrintAccentPrimary = "#4F759B";  // CS-Blue (primary headers)
        public const string PrintAccentSecondary = "#7291AF";// Blues Step 1 (subheadings)
        public const string PrintTextPrimary = "#332F2E";    // CS-Grey (body text)
        public const string PrintTextSecondary = "#5C5958";  // Grays Step 1 (muted sub-information)
        public const string PrintBorder = "#D6D5D5";         // Grays Step 4 (table borders)
        public const string PrintBgLight = "#FDFDFF";        // Whites Step 4 (zebra striping)
        public const string PrintBgCard = "#DFDAD6";         // Browns Step 4 (stat cards background)
    }
}
