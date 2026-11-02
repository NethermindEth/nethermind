// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.RegularExpressions;

static partial class ScriptVersion
{
    internal static string Root()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(AppContext.GetData("EntryPointFilePath") as string) ?? Environment.CurrentDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "scripts", "version.cs"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Run from the repository or one of its subdirectories.");
    }

    // Duplicate properties produce a multiline value that Read's tag-safe validation rejects.
    internal static string Property(string text, string name) => string.Join('\n', (name switch
    {
        "VersionPrefix" => Prefix(),
        "VersionSuffix" => Suffix(),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    }).Matches(text).Select(match => match.Groups[1].Value));

    [GeneratedRegex(@".*<VersionPrefix>(.*)</VersionPrefix>.*")]
    private static partial Regex Prefix();
    [GeneratedRegex(@".*<VersionSuffix>(.*)</VersionSuffix>.*")]
    private static partial Regex Suffix();
    [GeneratedRegex(@"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}\z")]
    private static partial Regex ValidVersion();

    internal static string Read(string file)
    {
        if (!File.Exists(file)) throw new InvalidOperationException($"error: '{file}' does not exist");
        var text = File.ReadAllText(file);
        var prefix = Property(text, "VersionPrefix");
        var suffix = Property(text, "VersionSuffix");
        if (prefix.Length == 0) throw new InvalidOperationException($"error: '{file}' declares no <VersionPrefix>");
        var version = suffix.Length == 0 ? prefix : $"{prefix}-{suffix}";
        if (!ValidVersion().IsMatch(version))
            throw new InvalidOperationException($"error: '{file}' produced an unusable version: {version}");
        return version;
    }
}
