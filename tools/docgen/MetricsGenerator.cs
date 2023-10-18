// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Nethermind.DocGen;

internal static partial class MetricsGenerator
{
    private static readonly Regex _regex = TransformRegex();

    internal static void Generate()
    {
        var startMark = "<!--[start autogen]-->";
        var endMark = "<!--[end autogen]-->";
        var fileName = "metrics.md";
        var excluded = new[] { "AccountAbstraction", "Mev" };

        var types = Directory
            .GetFiles(AppDomain.CurrentDomain.BaseDirectory, "Nethermind.*.dll")
            .SelectMany(a => Assembly.LoadFile(a).GetExportedTypes())
            .Where(t => t.Name.Equals("Metrics", StringComparison.Ordinal) &&
                !excluded.Any(x => t.FullName?.Contains(x, StringComparison.Ordinal) ?? false))
            .OrderBy(t => GetNamespace(t.FullName));
        
        using var readStream = new StreamReader(File.OpenRead(fileName));
        using var writeStream = new StreamWriter(File.OpenWrite($"~{fileName}"));

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
    }

    private static void WriteMarkdown(StreamWriter file, Type metricsType)
    {
        var props = metricsType
            .GetProperties()
            .OrderBy(p => p.Name);

        if (!props.Any())
            return;

        file.WriteLine($"""
            <details>
            <summary className="nd-details-heading">

            #### {GetNamespace(metricsType.FullName)}

            </summary>
            <p>

            """);

        foreach (var prop in props)
        {
            var attr = prop.GetCustomAttribute<DescriptionAttribute>();
            var param = _regex.Replace(prop.Name, m => $"_{m.Value.ToLowerInvariant()}");

            file.WriteLine($"- **`nethermind{param}`**");

            if (!string.IsNullOrWhiteSpace(attr?.Description))
                file.WriteLine($"""
                      
                      {attr.Description}

                    """);
        }

        file.WriteLine("""

            </p>
            </details>

            """);
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
