using System.Text.RegularExpressions;

public class DialogueTagParser
{
    public static bool TryParseAutoTag(ref string text, out float delay)
    {
        delay = 0f;

        // ∆•≈‰ <auto> ªÚ <auto=1.5>
        Match match = Regex.Match(text, @"<auto(?:=(\d+(\.\d+)?))?>");

        if (match.Success)
        {
            // Ã·»°—”≥Ÿ
            if (match.Groups[1].Success)
            {
                float.TryParse(match.Groups[1].Value, out delay);
            }

            // “∆≥˝tag
            text = text.Replace(match.Value, "");

            return true;
        }

        return false;
    }
}