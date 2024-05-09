// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript;
using System.Text.Json;

namespace Nethermind.DocGen;

internal static class DatabaseSizeGenerator
{
    static string startMark = "<!--[start autogen]-->";
    static string endMark = "<!--[end autogen]-->";
    static string fileName = "database.md";
    static string chainSizesDir = "chainSizes";

    static List<string> dbsToSave = new List<string>()
    {
        "state",
        "receipts",
        "blocks",
        "bloom",
        "headers",
        "code",
        "blobTransactions"
    };

    internal static void Generate()
    {
        var chainSizesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, chainSizesDir);
        var chains = Directory
            .GetFiles(chainSizesPath)
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(CustomOrderingLogic)
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

        WriteMarkdown(writeStream, chains);

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
        
        file.WriteLine("<Tabs>");
    }

    private static void WriteChainSize(StreamWriter file, string chain)
    {
        var upperCaseChainName = char.ToUpper(chain[0]) + chain[1..];
        file.WriteLine($"<TabItem value=\"{chain}\" label=\"{upperCaseChainName}\">");
        file.WriteLine("");
        file.WriteLine($"| **Database** | **{upperCaseChainName}** |");
        file.WriteLine("|-----------|------------|");

        using(JsonDocument doc = JsonDocument.Parse(File.ReadAllText($"{chainSizesDir}/{chain}.json")))
        {
            JsonElement root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach(string db in dbsToSave)
                {
                    string size = root.EnumerateObject().FirstOrDefault(x => x.Name.Contains(db)).Value.ToString();
                    string dbToUpper = char.ToUpper(db[0]) + db[1..];
                    file.WriteLine($"| {dbToUpper} | {size} |");
                }
            }

            file.WriteLine($"| Other | ... |");
            string totalSize = root.EnumerateObject().FirstOrDefault(x => x.Name.EndsWith("nethermind_db")).Value.ToString();
            file.WriteLine($"| **TOTAL** | **{totalSize}** |");

            file.WriteLine("");
            file.WriteLine("</TabItem>");
        }

    }

    private static int CustomOrderingLogic(string? filename)
    {
        if (filename == null) return 99;
        if (filename.StartsWith("mainnet")) return 1;
        if (filename.StartsWith("sepolia")) return 2;
        if (filename.StartsWith("holesky")) return 3;
        if (filename.StartsWith("gnosis")) return 4;
        if (filename.StartsWith("chiado")) return 5;
        if (filename.StartsWith("energyweb")) return 6;
        if (filename.StartsWith("volta")) return 7;
        return 99;
    }
}
