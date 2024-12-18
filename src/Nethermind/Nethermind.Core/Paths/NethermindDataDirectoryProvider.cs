using System;
using System.IO;

public class NethermindDataDirectoryProvider
{
    private readonly string _defaultBasePath;

    public NethermindDataDirectoryProvider()
    {
        // Use XDG_DATA_HOME if set, otherwise fallback to ~/.local/share
        string xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ??
                              Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        // Set the default base path for Nethermind data
        _defaultBasePath = Path.Combine(xdgDataHome, "nethermind");
    }

    public string GetDefaultBasePath() => _defaultBasePath;

    public string GetDbPath(string dbName)
    {
        return Path.Combine(_defaultBasePath, "db", dbName);
    }

    public string GetLogsPath()
    {
        return Path.Combine(_defaultBasePath, "logs");
    }

    // Add other path-related methods as needed
}
