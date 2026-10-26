#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include script-process.helpers.cs

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    if (args.Length is < 1 or > 2 || string.IsNullOrEmpty(args[0]))
    {
        Console.Error.WriteLine("Usage: prune-runtimes.cs <target-rid> [bin-root]");
        return 1;
    }

    var rid = args[0];
    var root = Path.GetFullPath(args.Length == 2 ? args[1] : "src/Nethermind/artifacts/bin");
    if (!Directory.Exists(root))
    {
        Console.WriteLine($"::error::bin root not found: {root}");
        return 1;
    }

    var before = await DiskUsage(root, cancellation.Token);
    var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
    var candidates = Directory.EnumerateDirectories(root, "*", options)
        .Where(path => Path.GetFileName(Path.GetDirectoryName(path)) == "runtimes")
        .Order(StringComparer.Ordinal).ToArray();
    // Bare-OS folders hold managed assemblies used by the target RID's fallback graph.
    var foreign = candidates.Where(path => Path.GetFileName(path).Contains('-') && Path.GetFileName(path) != rid).ToArray();
    foreach (var path in foreign)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    if (!candidates.Any(path => Path.GetFileName(path) == rid && Directory.Exists(path)))
        Console.WriteLine($"::warning::no runtimes/{rid} folder present after prune for rid={rid}");
    Console.WriteLine($"prune-runtimes[{rid}]: {before}MB -> {await DiskUsage(root, cancellation.Token)}MB (removed {foreign.Length} foreign RID dirs)");
    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 130; }
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static async Task<string> DiskUsage(string root, CancellationToken cancellationToken)
{
    try
    {
        var result = await ScriptProcess.Run("du", ["-sm", root], cancellationToken);
        return result.Code == 0 ? result.Output.Split('\t')[0].Trim() : "?";
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return "?";
    }
}
