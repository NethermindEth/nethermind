// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.DocGen;

internal static class DBSizeGenerator
{
    private const string _chainSizesDir = "chainSizes";

    private static readonly List<string> _dbList =
    [
        "state",
        "receipts",
        "blocks",
        "bloom",
        "headers",
        "code",
        "blobTransactions"
    ];

    internal static void Generate()
    {
        IList<string> chainOrder =
        [
            "mainnet",
            "sepolia",
            "holesky",
            "gnosis",
            "chiado",
            "energyweb",
            "volta"
        ];
        var startMark = "<!--[start autogen]-->";
        var endMark = "<!--[end autogen]-->";
        var fileName = "database.md";

        var chainSizesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _chainSizesDir);
        var chains = Directory
            .GetFiles(chainSizesPath)
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(c =>
            {
                var i = chainOrder.IndexOf(c!);

                return i == -1 ? int.MaxValue : i;
            })
            .ToList();

        File.Delete($"~{fileName}");

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

        WriteMarkdown(writeStream, chains!);

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

        File.Move($"~{fileName}", fileName, true);

        Console.WriteLine($"Updated {fileName}");
    }

    private static void WriteMarkdown(StreamWriter file, List<string> chains)
    {
        file.WriteLine("<Tabs>");

        foreach (var chain in chains)
            WriteChainSize(file, chain);

        file.WriteLine("""
            </Tabs>

            """);
    }

    private static void WriteChainSize(StreamWriter file, string chain)
    {
        using var json = JsonDocument.Parse(File.ReadAllText($"{_chainSizesDir}/{chain}.json"));

        if (json.RootElement.ValueKind != JsonValueKind.Object)
            return;

        var chainCapitalized = $"{char.ToUpper(chain[0])}{chain[1..]}";

        file.WriteLine($"""
            <TabItem value="{chain}" label="{chainCapitalized}">

            """);

        var items = json.RootElement.EnumerateObject();

        foreach (var db in _dbList)
        {
            var size = items
                .FirstOrDefault(e => e.Name.Contains(db, StringComparison.Ordinal))
                .Value.ToString();

            file.WriteLine($"- `{db}`: {FormatSize(size)}");
        }

        var totalSize = items
            .FirstOrDefault(e => e.Name.EndsWith("nethermind_db", StringComparison.Ordinal))
            .Value.ToString();

        file.WriteLine($"""
            - ...
            - **Total: {FormatSize(totalSize)}**

            </TabItem>
            """);
    }

    private static string FormatSize(string value) => value
        .Replace("G", " GB")
        .Replace("M", " MB")
        .Replace("K", " KB");
}
