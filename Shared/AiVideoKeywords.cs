using System.Text.RegularExpressions;

namespace YouTube.Shared;

public static class AiVideoKeywords
{
    private static readonly Regex Pattern = new(
        @"\b(?:AI|artificial\s+intelligence|machine\s+learning|deep\s+learning|neural\s+network|LLM|generative\s+AI|ChatGPT)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static bool Matches(string title, string description) =>
        Pattern.IsMatch(title) || Pattern.IsMatch(description);
}
