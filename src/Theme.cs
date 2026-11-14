using System.Drawing;

namespace AiUsageWidget
{
    // The colour palette the painter draws with. Dark holds exactly the colours the
    // painter used to hard-code; Light mirrors the same roles with light values whose
    // text (plain and dim) reads at 4.5:1 or better on both background and card, and
    // whose bar colours stay clearly distinguishable from the card.
    public sealed class Theme
    {
        public readonly Color Background;
        public readonly Color Card;
        public readonly Color Line;
        public readonly Color Text;
        public readonly Color DimText;
        public readonly Color Green;
        public readonly Color Yellow;
        public readonly Color Red;
        public readonly Color Accent;
        public readonly Color ChipText;   // glyph on the accent-filled (active) chip

        Theme(Color background, Color card, Color line, Color text, Color dimText,
            Color green, Color yellow, Color red, Color accent, Color chipText)
        {
            Background = background;
            Card = card;
            Line = line;
            Text = text;
            DimText = dimText;
            Green = green;
            Yellow = yellow;
            Red = red;
            Accent = accent;
            ChipText = chipText;
        }

        public static readonly Theme Dark = new Theme(
            FromHtml("#1E1F22"), FromHtml("#26282C"), FromHtml("#3A3D44"), FromHtml("#E6E6E6"),
            FromHtml("#8B8F98"), FromHtml("#3FB950"), FromHtml("#D29922"), FromHtml("#F85149"),
            FromHtml("#4C8DFF"), Color.White);

        public static readonly Theme Light = new Theme(
            FromHtml("#F6F8FA"), FromHtml("#FFFFFF"), FromHtml("#D0D7DE"), FromHtml("#1F2328"),
            FromHtml("#59636E"), FromHtml("#1A7F37"), FromHtml("#9A6700"), FromHtml("#CF222E"),
            FromHtml("#0969DA"), Color.White);

        static Color FromHtml(string hex)
        {
            return ColorTranslator.FromHtml(hex);
        }
    }
}
