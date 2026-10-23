// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.RegularExpressions;

static class ScriptVersion
{
    internal static string Root()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "scripts", "Version.cs"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Run from the repository or one of its subdirectories.");
    }

    internal static string Property(string text, string name) => string.Join('\n', Regex.Matches(text, $@".*<{name}>(.*)</{name}>.*").Select(match => match.Groups[1].Value));

    internal static string Read(string file)
    {
        if (!File.Exists(file)) throw new InvalidOperationException($"error: '{file}' does not exist");
        var text = File.ReadAllText(file);
        var prefix = Property(text, "VersionPrefix");
        var suffix = Property(text, "VersionSuffix");
        if (prefix.Length == 0) throw new InvalidOperationException($"error: '{file}' declares no <VersionPrefix>");
        var version = suffix.Length == 0 ? prefix : $"{prefix}-{suffix}";
        if (!Regex.IsMatch(version, @"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}\z"))
            throw new InvalidOperationException($"error: '{file}' produced an unusable version: {version}");
        return version;
    }
}
