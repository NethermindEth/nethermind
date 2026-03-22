// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;

/// <summary>
/// Extracts CPU sample instruction pointers from perfcollect's perf.data.txt
/// and writes them as a simple .spgo file (one hex IP per line) that a patched
/// dotnet-pgo can read alongside the .etlx for SPGO block count attribution.
/// </summary>
static class SpgoExtractor
{
    public static int Extract(string traceZipPath, string outputPath)
    {
        if (!traceZipPath.EndsWith(".trace.zip", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Input must be a .trace.zip file: {traceZipPath}");
            return 1;
        }

        Console.WriteLine($"Extracting perf sample IPs from {traceZipPath}...");

        using var zip = ZipFile.OpenRead(traceZipPath);
        var perfEntry = zip.Entries.FirstOrDefault(e => e.Name == "perf.data.txt");
        if (perfEntry == null)
        {
            Console.Error.WriteLine("No perf.data.txt found in .trace.zip");
            return 1;
        }

        int sampleCount = 0;
        int frameCount = 0;

        using (var stream = perfEntry.Open())
        using (var reader = new StreamReader(stream))
        using (var writer = new StreamWriter(outputPath))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                // Stack frame lines start with \t and contain a hex IP:
                //     7dc812345678 MethodName+0x1a3 (/path/to/module)
                if (line.Length > 0 && line[0] == '\t')
                {
                    var trimmed = line.AsSpan().TrimStart();
                    int spaceIdx = trimmed.IndexOf(' ');
                    if (spaceIdx > 0 &&
                        ulong.TryParse(trimmed[..spaceIdx], NumberStyles.HexNumber, null, out _))
                    {
                        // Write only the leaf IP (first frame of each sample)
                        // dotnet-pgo's AttributeSamplesToIP only uses the leaf IP
                        // for basic block attribution
                        frameCount++;

                        // Only write the first frame per sample (leaf IP)
                        // A new sample starts with a non-tab line
                        writer.WriteLine(trimmed[..spaceIdx]);
                        sampleCount++;

                        // Skip remaining frames until next sample header
                        while ((line = reader.ReadLine()) != null && line.Length > 0 && line[0] == '\t')
                            frameCount++;
                    }
                }
            }
        }

        Console.WriteLine($"  {sampleCount:N0} samples extracted ({frameCount:N0} total frames)");
        Console.WriteLine($"  Output: {outputPath}");

        return sampleCount > 0 ? 0 : 1;
    }
}
