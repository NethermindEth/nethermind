// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

internal static class ResourceSampling
{
    internal static JsonObject Sample(string cgroup, Func<bool> stop, Action delay)
    {
        var started = Stopwatch.GetTimestamp();
        var cpuStart = ReadValues(Path.Combine(cgroup, "cpu.stat")).GetValueOrDefault("usage_usec");
        var throttledStart = ReadValues(Path.Combine(cgroup, "cpu.stat")).GetValueOrDefault("throttled_usec");
        var ioStart = ReadIo(Path.Combine(cgroup, "io.stat"));
        var pressureStart = new[] { "cpu", "io", "memory" }.ToDictionary(name => name, name => ReadPressure(Path.Combine(cgroup, name + ".pressure")));
        var memory = new Measurements();
        var anon = new Measurements();
        var file = new Measurements();
        var peakCores = 0.0;
        var lastTime = started;
        var lastCpu = cpuStart;
        while (!stop())
        {
            delay();
            var now = Stopwatch.GetTimestamp();
            if (long.TryParse(ReadText(Path.Combine(cgroup, "memory.current")).Trim(), CultureInfo.InvariantCulture, out var current) && current >= 0) memory.Add(current);
            var memoryStat = ReadValues(Path.Combine(cgroup, "memory.stat"));
            if (memoryStat.TryGetValue("anon", out var anonymous)) anon.Add(anonymous);
            if (memoryStat.TryGetValue("file", out var cached)) file.Add(cached);
            var cpuNow = ReadValues(Path.Combine(cgroup, "cpu.stat")).GetValueOrDefault("usage_usec");
            var span = Stopwatch.GetElapsedTime(lastTime, now).TotalSeconds;
            if (span > 0) peakCores = Math.Max(peakCores, (cpuNow - lastCpu) / 1e6 / span);
            lastTime = now; lastCpu = cpuNow;
        }
        var wall = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var cpuSeconds = (ReadValues(Path.Combine(cgroup, "cpu.stat")).GetValueOrDefault("usage_usec") - cpuStart) / 1e6;
        var ioEnd = ReadIo(Path.Combine(cgroup, "io.stat"));
        var result = new JsonObject
        {
            ["wall_seconds"] = Math.Round(wall, 3), ["samples"] = memory.Count,
            ["cpu_seconds"] = Math.Round(cpuSeconds, 3), ["cpu_avg_cores"] = wall > 0 ? Math.Round(cpuSeconds / wall, 3) : 0,
            ["cpu_peak_cores"] = Math.Round(peakCores, 3),
            ["cpu_throttled_usec"] = ReadValues(Path.Combine(cgroup, "cpu.stat")).GetValueOrDefault("throttled_usec") - throttledStart,
            ["memory_avg_bytes"] = memory.Average ?? 0, ["memory_peak_bytes"] = memory.Maximum ?? 0,
            ["memory_anon_avg_bytes"] = anon.Average, ["memory_anon_peak_bytes"] = anon.Maximum, ["memory_file_avg_bytes"] = file.Average,
            ["io_read_bytes"] = ioEnd.Read - ioStart.Read, ["io_write_bytes"] = ioEnd.Write - ioStart.Write,
            ["requests"] = 0
        };
        foreach (var (name, start) in pressureStart)
            result["stall_" + name + "_usec"] = ReadPressure(Path.Combine(cgroup, name + ".pressure")) - start;
        return result;
    }

    internal static void Normalize(string path, long requests)
    {
        JsonObject summary;
        try { summary = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new IOException("sample is not a resource summary"); }
        catch (System.Text.Json.JsonException) { throw new IOException("cannot read sample: invalid JSON"); }
        if (!summary.ContainsKey("cpu_seconds")) throw new IOException("sample is not a resource summary");
        summary["requests"] = requests;
        summary.Remove("cpu_ms_per_request"); summary.Remove("io_read_bytes_per_request");
        if (requests > 0)
        {
            summary["cpu_ms_per_request"] = Math.Round(summary["cpu_seconds"]!.GetValue<double>() * 1000 / requests, 4);
            summary["io_read_bytes_per_request"] = Math.Round(summary["io_read_bytes"]!.GetValue<double>() / requests, 1);
        }
        Write(path, summary);
    }

    internal static void Write(string path, JsonObject summary)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var ordered = new JsonObject(summary.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone())));
        File.WriteAllText(path, ordered.ToJsonString());
        Console.WriteLine(FormattableString.Invariant($"resources: {summary["cpu_avg_cores"]} avg cores, {summary["memory_peak_bytes"]!.GetValue<long>() / 1e9:F2} GB peak, {summary["cpu_ms_per_request"]?.ToString() ?? "n/a"} CPU-ms/request"));
    }

    private static string ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    private static Dictionary<string, long> ReadValues(string path)
    {
        var values = new Dictionary<string, long>();
        foreach (var line in ReadText(path).Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var value)) values[parts[0]] = value;
        }
        return values;
    }

    private static long? ReadPressure(string path)
    {
        foreach (var line in ReadText(path).Split('\n'))
            if (line.StartsWith("some", StringComparison.Ordinal))
                foreach (var field in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    if (field.StartsWith("total=", StringComparison.Ordinal))
                        return long.TryParse(field[6..], CultureInfo.InvariantCulture, out var value) ? value : null;
        return null;
    }

    private static (long Read, long Write) ReadIo(string path)
    {
        var read = 0L; var write = 0L;
        foreach (var field in ReadText(path).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (field.StartsWith("rbytes=", StringComparison.Ordinal))
            {
                if (!long.TryParse(field[7..], CultureInfo.InvariantCulture, out var value)) break;
                read += value;
            }
            else if (field.StartsWith("wbytes=", StringComparison.Ordinal))
            {
                if (!long.TryParse(field[7..], CultureInfo.InvariantCulture, out var value)) break;
                write += value;
            }
        }
        return (read, write);
    }

    private sealed class Measurements
    {
        internal long Count { get; private set; }
        private decimal _sum;
        internal long? Maximum { get; private set; }
        internal long? Average => Count > 0 ? (long)(_sum / Count) : null;
        internal void Add(long value) { Count++; _sum += value; Maximum = Maximum is { } previous ? Math.Max(previous, value) : value; }
    }
}
