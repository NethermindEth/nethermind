namespace Nethermind.PatternAnalyzer.Plugin.Types;

public enum ProcessingMode
{
    Sequential,
    Bulk
}

public class ProcessingModeParser
{
    public static ProcessingMode Parse(string modeStr)
    {
        if (!Enum.TryParse(modeStr, true, out ProcessingMode mode))
            throw new ArgumentException($"Invalid processing mode: {modeStr}");
        return mode;
    }
}