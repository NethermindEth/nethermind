// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.RegularExpressions;

internal static partial class BenchmarkPaths
{
    internal static void AssertSane(string path, string label)
    {
        if (!path.StartsWith('/')) throw new ArgumentException($"{label} '{path}' must be an absolute path");
        if (path.Contains("..", StringComparison.Ordinal)) throw new ArgumentException($"{label} '{path}' must not contain '..'");
        var trimmed = path.Trim('/');
        if (trimmed.Length == 0) throw new ArgumentException($"{label} must not be '/'");
        if (!trimmed.Contains('/')) throw new ArgumentException($"{label} '{path}' is too shallow (need at least two path components)");
    }

    internal static bool Contains(string parent, string child) => child.TrimEnd('/') == parent.TrimEnd('/') || child.StartsWith(parent.TrimEnd('/') + "/", StringComparison.Ordinal);

    internal static async Task<string> CanonicalAsync(string path, bool exists, CancellationToken cancellationToken)
    {
        var result = await BenchmarkProcess.RunAsync("realpath", [exists ? "-e" : "-m", "--", path], cancellationToken);
        if (result.Code != 0) throw new IOException($"cannot canonicalize '{path}': {result.Error.Trim()}");
        return result.Output.TrimEnd('\r', '\n');
    }

    internal static async Task<(string Database, string Scratch)> GuardAsync(string database, string scratch, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(database)) throw new IOException($"guard_paths: DB_SOURCE '{database}' is not a directory");
        database = await CanonicalAsync(database, true, cancellationToken);
        scratch = await CanonicalAsync(scratch, false, cancellationToken);
        AssertSane(database, "DB_SOURCE"); AssertSane(scratch, "SCRATCH_ROOT");
        if (Contains(scratch, database)) throw new IOException("DB_SOURCE must not be inside SCRATCH_ROOT");
        if (Contains(database, scratch)) throw new IOException("SCRATCH_ROOT must not be inside DB_SOURCE");
        return (database, scratch);
    }

    internal static string[] MountsUnder(string path, string mountsPath = "/proc/self/mounts") =>
        File.ReadLines(mountsPath).Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(parts => parts.Length >= 2)
            .Select(parts => MountEscapeRegex().Replace(parts[1], match => ((char)Convert.ToInt32(match.Groups[1].Value, 8)).ToString()))
            .Where(mount => Contains(path, mount)).OrderDescending(StringComparer.Ordinal).ToArray();

    internal static async Task ReapAsync(IEnumerable<string> prefixes, CancellationToken cancellationToken)
    {
        foreach (var prefix in prefixes)
        {
            var listed = await BenchmarkProcess.RunAsync("docker", ["ps", "-aq", "--filter", "name=^" + prefix], cancellationToken);
            if (listed.Code != 0 || string.IsNullOrWhiteSpace(listed.Output)) continue;
            BenchmarkProcess.Log($"Reaping stale container(s) matching '{prefix}*'...");
            await BenchmarkProcess.RunAsync("docker", new[] { "rm", "-fv" }.Concat(listed.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), cancellationToken);
        }
    }
    [GeneratedRegex(@"\\([0-7]{3})")]
    private static partial Regex MountEscapeRegex();
}
