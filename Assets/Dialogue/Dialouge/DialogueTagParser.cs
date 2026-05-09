using System.Text.RegularExpressions;

/// <summary>
/// Parses and removes custom control tags from raw dialogue lines. These are not TMP rich-text tags.
/// </summary>
public class DialogueTagParser
{
    /// <summary>
    /// Matches <c>&lt;anim=StateName&gt;</c>, removes it from <paramref name="text"/>, and outputs the state name.
    /// Call repeatedly to strip several tags in left-to-right order.
    /// </summary>
    /// <param name="stateName">Animator state name passed to <see cref="PlayerController.SwitchAnimationTo"/>.</param>
    public static bool TryParseAnimTag(ref string text, out string stateName)
    {
        stateName = null;
        Match match = Regex.Match(text, @"<anim=([^>]+)>");
        if (!match.Success)
            return false;

        stateName = match.Groups[1].Value.Trim();
        text = text.Replace(match.Value, string.Empty);
        return !string.IsNullOrEmpty(stateName);
    }

    /// <summary>
    /// Matches <c>&lt;auto&gt;</c> or <c>&lt;auto=seconds&gt;</c>, removes the tag, and sets <paramref name="delay"/> when a number is present.
    /// </summary>
    public static bool TryParseAutoTag(ref string text, out float delay)
    {
        delay = 0f;

        Match match = Regex.Match(text, @"<auto(?:=(\d+(\.\d+)?))?>");

        if (!match.Success)
            return false;

        if (match.Groups[1].Success)
            float.TryParse(match.Groups[1].Value, out delay);

        text = text.Replace(match.Value, string.Empty);
        return true;
    }
}
