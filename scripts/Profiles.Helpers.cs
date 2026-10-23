// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text.RegularExpressions;

internal static class Profiles
{
    internal static bool IsManaged(string frame) => Regex.IsMatch(frame, @"\[[A-Za-z0-9_.-]+\]\s.*::");

    internal static Dictionary<string, long> Fold(TextReader input)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var frames = new List<string>();
        var command = "";
        void Flush()
        {
            if (frames.Count == 0) return;
            frames.Reverse();
            var stack = (command.Length > 0 ? command + ";" : "") + string.Join(';', frames);
            counts[stack] = counts.GetValueOrDefault(stack) + 1;
            frames.Clear();
        }
        while (input.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) { Flush(); continue; }
            if (!char.IsWhiteSpace(line[0]))
            {
                Flush();
                command = Regex.Split(line, @"\s+")[0];
                continue;
            }
            line = line.TrimEnd();
            var dso = Regex.Match(line, @"\(([^)]*)\)$");
            if (dso.Success) line = line[..dso.Index];
            line = Regex.Replace(line, @"^\s+[0-9a-fA-F]+\s+", "");
            line = Regex.Replace(line, @"\+0x[0-9a-fA-F]+\s*$", "").TrimEnd();
            if (line is "" or "[unknown]")
            {
                var library = dso.Groups[1].Value;
                line = library is "" or "[unknown]" ? "[unknown]" : $"[unknown] ({library[(library.LastIndexOf('/') + 1)..]})";
            }
            frames.Add(line);
        }
        Flush();
        return counts;
    }

    internal static IEnumerable<(string Stack, long Count)> Read(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            var match = Regex.Match(line, @"^(.*)\s+([0-9]+)\s*$");
            if (match.Success && long.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var count))
                yield return (match.Groups[1].Value, count);
        }
    }

    internal static (Dictionary<string, long> Counts, long Total) Aggregate(string path, bool inclusive)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new IOException($"'{path}' is missing or empty");
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var total = 0L;
        foreach (var (stack, count) in Read(path))
        {
            total = checked(total + count);
            var frames = stack.Split(';');
            foreach (var frame in inclusive ? frames.Distinct(StringComparer.Ordinal) : [frames[^1]])
                counts[frame] = checked(counts.GetValueOrDefault(frame) + count);
        }
        if (total <= 0) throw new IOException($"'{path}' has no positive sample counts");
        return (counts, total);
    }

    internal static string Display(string frame, int width) => frame.Length > width ? "..." + frame[^(width - 3)..] : frame;
}
