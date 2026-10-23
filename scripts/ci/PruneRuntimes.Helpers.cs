// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

internal static class RuntimePruner
{
    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length is < 1 or > 2 || string.IsNullOrEmpty(args[0]))
        {
            error.WriteLine("Usage: PruneRuntimes.cs <target-rid> [bin-root]");
            return 1;
        }

        var rid = args[0];
        var root = Path.GetFullPath(args.Length == 2 ? args[1] : "src/Nethermind/artifacts/bin");
        if (!Directory.Exists(root))
        {
            output.WriteLine($"::error::bin root not found: {root}");
            return 1;
        }

        var before = DiskUsage(root);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        var candidates = Directory.GetDirectories(root, "*", options)
            .Where(path => Path.GetFileName(Path.GetDirectoryName(path)) == "runtimes")
            .Order(StringComparer.Ordinal).ToArray();
        var foreign = candidates.Where(path => Path.GetFileName(path).Contains('-') && Path.GetFileName(path) != rid).ToArray();
        foreach (var path in foreign)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }

        if (!candidates.Any(path => Path.GetFileName(path) == rid && Directory.Exists(path)))
            output.WriteLine($"::warning::no runtimes/{rid} folder present after prune for rid={rid}");
        output.WriteLine($"prune-runtimes[{rid}]: {before}MB -> {DiskUsage(root)}MB (removed {foreign.Length} foreign RID dirs)");
        return 0;
    }

    private static string DiskUsage(string root)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("du")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            process.StartInfo.ArgumentList.Add("-sm");
            process.StartInfo.ArgumentList.Add(root);
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WhenAll(output, error).GetAwaiter().GetResult();
            return process.ExitCode == 0 ? output.GetAwaiter().GetResult().Split('\t')[0].Trim() : "?";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "?";
        }
    }
}
