// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;

/// <summary>
/// Extracts CPU sample data from perfcollect's perf.data.txt:
/// - .spgo file: one hex leaf IP per line for SPGO basic block attribution
/// - .callgraph file: callee_ip caller_ip pairs for Pettis-Hansen method layout
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

        string callGraphPath = Path.ChangeExtension(outputPath, ".callgraph");

        int sampleCount = 0;
        int frameCount = 0;
        int edgeCount = 0;

        using (var stream = perfEntry.Open())
        using (var reader = new StreamReader(stream))
        using (var spgoWriter = new StreamWriter(outputPath))
        using (var cgWriter = new StreamWriter(callGraphPath))
        {
            cgWriter.WriteLine("# callee_ip caller_ip");

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
                        frameCount++;
                        // Leaf IP for SPGO block attribution
                        string leafIp = trimmed[..spaceIdx].ToString();
                        spgoWriter.WriteLine(leafIp);
                        sampleCount++;

                        // Read caller frame (second frame) for call graph
                        line = reader.ReadLine();
                        if (line != null && line.Length > 0 && line[0] == '\t')
                        {
                            frameCount++;
                            var callerTrimmed = line.AsSpan().TrimStart();
                            int callerSpace = callerTrimmed.IndexOf(' ');
                            if (callerSpace > 0 &&
                                ulong.TryParse(callerTrimmed[..callerSpace], NumberStyles.HexNumber, null, out _))
                            {
                                cgWriter.Write(leafIp);
                                cgWriter.Write(' ');
                                cgWriter.WriteLine(callerTrimmed[..callerSpace]);
                                edgeCount++;
                            }

                            // Skip remaining frames until next sample header
                            while ((line = reader.ReadLine()) != null && line.Length > 0 && line[0] == '\t')
                                frameCount++;
                        }
                    }
                }
            }
        }

        Console.WriteLine($"  {sampleCount:N0} samples extracted ({frameCount:N0} total frames)");
        Console.WriteLine($"  {edgeCount:N0} caller-callee edges extracted");
        Console.WriteLine($"  Output: {outputPath}");
        Console.WriteLine($"  Output: {callGraphPath}");

        return sampleCount > 0 ? 0 : 1;
    }
}
