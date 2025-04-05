namespace Nethermind.PatternAnalyzer.Plugin.Types;

public enum SortOrder
{
    Unordered,
    Ascending,
    Descending
}

public class SortOrderParser
{
    public static SortOrder Parse(string input)
    {
        if (!Enum.TryParse<SortOrder>(input.Trim(), true, out var result))
            throw new ArgumentException($"Invalid sort order value: {input}.");

        return result;
    }
}
