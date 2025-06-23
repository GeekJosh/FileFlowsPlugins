namespace FileFlows.VideoNodes.Helpers;

/// <summary>
/// General helper
/// </summary>
public class GeneralHelper
{
    /// <summary>
    /// Checks if the input string represents a regular expression.
    /// </summary>
    /// <param name="input">The input string to check.</param>
    /// <returns>True if the input is a regular expression, otherwise false.</returns>
    public static bool IsRegex(string input)
        => new[] { "?", "|", "^", "$", "*" }.Any(input.Contains);
    
    /// <summary>
    /// Converts a bitrate (in bits per second) to a human-readable string, such as "1.5 Mbps".
    /// </summary>
    /// <param name="bitrate">The bitrate in bits per second.</param>
    /// <returns>A human-readable representation of the bitrate.</returns>
    public static string HumanizeBitrate(float bitrate)
    {
        string[] sizes = { "bps", "Kbps", "Mbps", "Gbps", "Tbps" };
        int order = 0;
        double len = bitrate;
        while (len >= 1000 && order < sizes.Length - 1)
        {
            order++;
            len /= 1000;
        }

        return $"{len:0.##} {sizes[order]}";
    }
}