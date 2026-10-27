// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

static partial class Sync
{
    [GeneratedRegex(@"^c3d-(standard|highmem)-(\d+)-lssd$")]
    private static partial Regex LocalSsdMachineRegex();

    internal static string Container(string network) => network.StartsWith("taiko-", StringComparison.Ordinal)
        ? "sedge-execution-taiko-client"
        : new[] { "base-", "world-", "op-" }.Any(prefix => network.StartsWith(prefix, StringComparison.Ordinal))
            ? "sedge-execution-op-l2-client" : "sedge-execution-client";

    internal static string L1(string network)
    {
        if (network.StartsWith("op-", StringComparison.Ordinal)) network = network[3..];
        return network.StartsWith("world-", StringComparison.Ordinal) ? network[6..] : network;
    }

    internal static JsonArray Select(JsonArray matrix, string filter)
    {
        var exact = matrix.Where(entry => entry!["network"]!.GetValue<string>() == filter).ToArray();
        var result = new JsonArray();
        foreach (var entry in filter.Length == 0 ? matrix : exact.Length > 0 ? exact : matrix.Where(entry => entry!["network"]!.GetValue<string>().Contains(filter, StringComparison.Ordinal)))
            result.Add(entry!.DeepClone());
        return result;
    }

    internal static IEnumerable<string> ShapeErrors(JsonArray matrix)
    {
        var bundled = new Dictionary<int, int> { [8] = 1, [16] = 1, [30] = 2, [60] = 4, [90] = 8, [180] = 16, [360] = 32 };
        foreach (var entry in matrix)
        {
            var type = entry!["machine_type"]!.GetValue<string>();
            var count = entry["local_ssd_count"]!.GetValue<int>();
            var match = LocalSsdMachineRegex().Match(type);
            var known = match.Success && int.TryParse(match.Groups[2].Value, out var size) && bundled.TryGetValue(size, out _);
            var prefix = $"{entry["network"]}: {type}";
            if (type.StartsWith("c3d-", StringComparison.Ordinal) && type.EndsWith("-lssd", StringComparison.Ordinal) && !known)
                yield return $"{prefix} is not a C3D -lssd shape this check knows; add its bundled count";
            else if (known && bundled[int.Parse(match.Groups[2].Value)] != count)
                yield return $"{prefix} bundles {bundled[int.Parse(match.Groups[2].Value)]} Local SSD(s), but local_ssd_count is {count}";
            else if (type.StartsWith("c3d-", StringComparison.Ordinal) && !type.EndsWith("-lssd", StringComparison.Ordinal) && count > 0)
                yield return $"{prefix} cannot attach Local SSD; use a -lssd type or local_ssd_count 0";
        }
    }

    internal static Process Start(string executable, IEnumerable<string> arguments, bool redirect = false)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = redirect };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException($"Cannot start {executable}");
    }

    internal static void Stop(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        process.WaitForExit();
    }
}
