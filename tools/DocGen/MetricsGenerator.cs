// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Nethermind.DocGen;

internal static partial class MetricsGenerator
{
    private static readonly Regex _regex = TransformRegex();

    internal static void Generate(string path)
    {
        path = Path.Join(path, "docs", "monitoring", "metrics");

        var startMark = "<!--[start autogen]-->";
        var endMark = "<!--[end autogen]-->";
        var excluded = Array.Empty<string>();
        var types = Directory
            .GetFiles(AppDomain.CurrentDomain.BaseDirectory, "Nethermind.*.dll")
            .SelectMany(a => Assembly.LoadFile(a).GetExportedTypes())
            .Where(t => t.Name.Equals("Metrics", StringComparison.Ordinal) &&
                !excluded.Any(x => t.FullName?.Contains(x, StringComparison.Ordinal) ?? false))
            .OrderBy(t => GetNamespace(t.FullName));
        var fileName = Path.Join(path, "metrics.md");
        var tempFileName = Path.Join(path, "~metrics.md");

        // Delete the temp file if it exists
        File.Delete(tempFileName);

        using var readStream = new StreamReader(File.OpenRead(fileName));
        using var writeStream = new StreamWriter(File.OpenWrite(tempFileName));

        writeStream.NewLine = "\n";

        var line = string.Empty;

        do
        {
            line = readStream.ReadLine();

            writeStream.WriteLine(line);
        }
        while (!line?.Equals(startMark, StringComparison.Ordinal) ?? false);

        writeStream.WriteLine();

        foreach (var type in types)
            WriteMarkdown(writeStream, type);

        var skip = true;

        for (line = readStream.ReadLine(); line is not null; line = readStream.ReadLine())
        {
            if (skip)
            {
                if (line?.Equals(endMark, StringComparison.Ordinal) ?? false)
                    skip = false;
                else
                    continue;
            }

            writeStream.WriteLine(line);
        }

        readStream.Close();
        writeStream.Close();

        File.Move(tempFileName, fileName, true);
        File.Delete(tempFileName);

        AnsiConsole.MarkupLine($"[green]Updated[/] {fileName}");
    }

    private static void WriteMarkdown(StreamWriter file, Type metricsType)
    {
        var props = metricsType
            .GetProperties()
            .OrderBy(p => p.Name);

        if (!props.Any())
            return;

        file.WriteLine($"""
            ### {GetNamespace(metricsType.FullName)}

            """);

        foreach (var prop in props)
        {
            var attr = prop.GetCustomAttribute<DescriptionAttribute>();
            var param = _regex.Replace(prop.Name, m => $"_{m.Value.ToLowerInvariant()}");

            file.WriteLine($"- #### `nethermind{param}` \\{{#{param[1..]}\\}}");

            if (!string.IsNullOrWhiteSpace(attr?.Description))
                file.WriteLine($"""
                      
                      {attr.Description}

                    """);
        }

        file.WriteLine();
    }

    private static string? GetNamespace(string? fullTypeName)
    {
        var ns = fullTypeName?
            .Replace("Nethermind.", null, StringComparison.Ordinal)
            .Replace(".Metrics", null, StringComparison.Ordinal);

        return ns switch
        {
            "Consensus.AuRa" => "Aura",
            "Init" => "Runner",
            "Merge.Plugin" => "Merge",
            "Trie.Pruning" => "Pruning",
            _ => ns
        };
    }

    [GeneratedRegex("([A-Z])")]
    private static partial Regex TransformRegex();
}
