// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include ScriptProcess.Helpers.cs

internal static class RuntimePruner
{
    internal static async Task<int> Run(string[] args, TextWriter output, TextWriter error)
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

        var before = await DiskUsage(root);
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
        output.WriteLine($"prune-runtimes[{rid}]: {before}MB -> {await DiskUsage(root)}MB (removed {foreign.Length} foreign RID dirs)");
        return 0;
    }

    private static async Task<string> DiskUsage(string root)
    {
        try
        {
            var result = await ScriptProcess.Run("du", ["-sm", root], CancellationToken.None);
            return result.Code == 0 ? result.Output.Split('\t')[0].Trim() : "?";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "?";
        }
    }
}
