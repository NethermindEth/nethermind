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
        var cpuStart = Counter(Path.Combine(cgroup, "cpu.stat"), "usage_usec");
        var throttledStart = Counter(Path.Combine(cgroup, "cpu.stat"), "throttled_usec");
        var ioStart = ReadIo(Path.Combine(cgroup, "io.stat"));
        var pressureStart = new[] { "cpu", "io", "memory" }.ToDictionary(name => name, name => ReadPressure(Path.Combine(cgroup, name + ".pressure")));
        var memory = new Measurements();
        var anon = new Measurements();
        var file = new Measurements();
        double? peakCores = null;
        var lastTime = started;
        var lastCpu = cpuStart;
        while (!stop())
        {
            delay();
            var now = Stopwatch.GetTimestamp();
            if (long.TryParse(ReadText(Path.Combine(cgroup, "memory.current"))?.Trim(), CultureInfo.InvariantCulture, out var current) && current >= 0) memory.Add(current);
            var memoryStat = ReadValues(Path.Combine(cgroup, "memory.stat"));
            if (memoryStat.TryGetValue("anon", out var anonymous)) anon.Add(anonymous);
            if (memoryStat.TryGetValue("file", out var cached)) file.Add(cached);
            var cpuNow = Counter(Path.Combine(cgroup, "cpu.stat"), "usage_usec");
            var span = Stopwatch.GetElapsedTime(lastTime, now).TotalSeconds;
            if (span > 0 && Delta(lastCpu, cpuNow) is { } cpuDelta) peakCores = Math.Max(peakCores ?? 0, cpuDelta / 1e6 / span);
            lastTime = now; lastCpu = cpuNow;
        }
        var wall = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var cpuSeconds = Delta(cpuStart, Counter(Path.Combine(cgroup, "cpu.stat"), "usage_usec")) / 1e6;
        var ioEnd = ReadIo(Path.Combine(cgroup, "io.stat"));
        var result = new JsonObject
        {
            ["wall_seconds"] = Math.Round(wall, 3), ["samples"] = memory.Count,
            ["cpu_seconds"] = cpuSeconds is { } cpu ? Math.Round(cpu, 3) : null,
            ["cpu_avg_cores"] = cpuSeconds is { } elapsed && wall > 0 ? Math.Round(elapsed / wall, 3) : null,
            ["cpu_peak_cores"] = peakCores is { } peak ? Math.Round(peak, 3) : null,
            ["cpu_throttled_usec"] = Delta(throttledStart, Counter(Path.Combine(cgroup, "cpu.stat"), "throttled_usec")),
            ["memory_avg_bytes"] = memory.Average, ["memory_peak_bytes"] = memory.Maximum,
            ["memory_anon_avg_bytes"] = anon.Average, ["memory_anon_peak_bytes"] = anon.Maximum, ["memory_file_avg_bytes"] = file.Average,
            ["io_read_bytes"] = Delta(ioStart.Read, ioEnd.Read), ["io_write_bytes"] = Delta(ioStart.Write, ioEnd.Write),
            ["requests"] = 0
        };
        foreach (var (name, start) in pressureStart)
            result["stall_" + name + "_usec"] = Delta(start, ReadPressure(Path.Combine(cgroup, name + ".pressure")));
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
            if (summary["cpu_seconds"] is JsonValue cpu && cpu.TryGetValue<double>(out var seconds) && double.IsFinite(seconds) && seconds >= 0)
                summary["cpu_ms_per_request"] = Math.Round(seconds * 1000 / requests, 4);
            if (summary["io_read_bytes"] is JsonValue io && io.TryGetValue<double>(out var bytes) && double.IsFinite(bytes) && bytes >= 0)
                summary["io_read_bytes_per_request"] = Math.Round(bytes / requests, 1);
        }
        Write(path, summary);
    }

    internal static void Write(string path, JsonObject summary)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var ordered = new JsonObject(summary.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone())));
        File.WriteAllText(path, ordered.ToJsonString());
        var peak = summary["memory_peak_bytes"] is JsonValue memory && memory.TryGetValue<long>(out var bytes) ? (bytes / 1e9).ToString("F2", CultureInfo.InvariantCulture) : "n/a";
        Console.WriteLine($"resources: {summary["cpu_avg_cores"]?.ToString() ?? "n/a"} avg cores, {peak} GB peak, {summary["cpu_ms_per_request"]?.ToString() ?? "n/a"} CPU-ms/request");
    }

    private static long? Counter(string path, string name) => ReadValues(path).TryGetValue(name, out var value) ? value : null;
    private static long? Delta(long? start, long? end) => start is >= 0 && end >= start ? end - start : null;

    private static string? ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static Dictionary<string, long> ReadValues(string path)
    {
        var values = new Dictionary<string, long>();
        foreach (var line in (ReadText(path) ?? "").Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var value) && value >= 0) values[parts[0]] = value;
        }
        return values;
    }

    private static long? ReadPressure(string path)
    {
        foreach (var line in (ReadText(path) ?? "").Split('\n'))
            if (line.StartsWith("some", StringComparison.Ordinal))
                foreach (var field in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    if (field.StartsWith("total=", StringComparison.Ordinal))
                        return long.TryParse(field[6..], CultureInfo.InvariantCulture, out var value) ? value : null;
        return null;
    }

    private static (long? Read, long? Write) ReadIo(string path)
    {
        var text = ReadText(path);
        if (text is null) return (null, null);
        var read = 0L; var write = 0L;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            long? readBytes = null; long? writeBytes = null;
            foreach (var field in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (field.StartsWith("rbytes=", StringComparison.Ordinal) && long.TryParse(field[7..], CultureInfo.InvariantCulture, out var r) && r >= 0) readBytes = r;
                if (field.StartsWith("wbytes=", StringComparison.Ordinal) && long.TryParse(field[7..], CultureInfo.InvariantCulture, out var w) && w >= 0) writeBytes = w;
            }
            if (readBytes is null || writeBytes is null || readBytes > long.MaxValue - read || writeBytes > long.MaxValue - write) return (null, null);
            read += readBytes.Value; write += writeBytes.Value;
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
