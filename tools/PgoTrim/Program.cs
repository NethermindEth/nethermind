// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Compression;
using System.Text;

// Trims DOTNET_WritePGOData .jit files by removing cold methods.
// Keeps methods with any block count >= threshold OR total edge count >= edgeThreshold.
// Preserves all PGO data for kept methods:
//   - All block/edge counts (kind 385) including zeros for cold-path signal
//   - All type/method histograms (kind 177/195/196) including all-NULL (required by JIT reader)
//   - GetLikelyClass/Method entries (kind 561/578)
// Strips:
//   - Cold methods below thresholds
//
// Usage: dotnet run -- <input.jit> [output.jit.gz] [--min-block 100] [--min-edge 250]

int minBlock = 100;
int minEdge = 250;
string? inputPath = null;
string? outputPath = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--min-block" when i + 1 < args.Length:
            minBlock = int.Parse(args[++i]);
            break;
        case "--min-edge" when i + 1 < args.Length:
            minEdge = int.Parse(args[++i]);
            break;
        default:
            if (inputPath is null) inputPath = args[i];
            else if (outputPath is null) outputPath = args[i];
            break;
    }
}

if (inputPath is null)
{
    Console.Error.WriteLine("Usage: PgoTrim <input.jit> [output.jit.gz] [--min-block N] [--min-edge N]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Trims DOTNET_WritePGOData .jit files, keeping only hot methods.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Options:");
    Console.Error.WriteLine("  --min-block N  Keep methods with any block/edge count >= N (default: 100)");
    Console.Error.WriteLine("  --min-edge N   Also keep methods with total edge count >= N (default: 250)");
    return 1;
}

outputPath ??= Path.ChangeExtension(inputPath, ".trimmed.jit.gz");

// InstrumentationKind values from coreclr:
// 385 = BasicBlockIntCount (block/edge counts)
// 177 = HandleHistogramIntCount (hit count for type/method profile site)
// 195 = HandleHistogramTypes (type handle array — Count = array length)
// 196 = HandleHistogramMethods (method handle array — Count = array length)
// 561 = GetLikelyClass
// 578 = GetLikelyMethod

int totalMethods = 0;
int keptMethods = 0;
long edgesKept = 0;
long histogramsKept = 0;
long histogramsAllNull = 0;

// Read entire file into lines for indexed access (histograms need lookahead)
string[] lines = File.ReadAllLines(inputPath);
List<MethodRecord> methods = [];
MethodRecord? current = null;
int lineIdx = 0;

while (lineIdx < lines.Length)
{
    string line = lines[lineIdx];

    // Method header
    if (line.StartsWith("@@@ codehash"))
    {
        if (current is not null)
        {
            FlushPending(current);
            methods.Add(current);
        }

        current = new MethodRecord { Header = line };
        totalMethods++;
        lineIdx++;
        continue;
    }

    if (current is null)
    {
        lineIdx++;
        continue;
    }

    if (line.StartsWith("MethodName:"))
    {
        current.MethodName = line;
        lineIdx++;
        continue;
    }

    if (line.StartsWith("Signature:"))
    {
        current.Signature = line;
        lineIdx++;
        continue;
    }

    // Schema line — dispatch by kind
    if (line.StartsWith("Schema InstrumentationKind"))
    {
        int kind = ParseKind(line);
        int count = ParseCount(line);

        switch (kind)
        {
            case 385: // BasicBlockIntCount — schema + 1 value line
                {
                    if (lineIdx + 1 < lines.Length && long.TryParse(lines[lineIdx + 1].Trim(), out long hitCount))
                    {
                        current.TotalEdgeCount += hitCount;
                        if (hitCount > current.MaxBlockCount)
                            current.MaxBlockCount = hitCount;

                        current.Entries.Add(line);
                        current.Entries.Add(lines[lineIdx + 1]);
                    }
                    lineIdx += 2;
                    break;
                }

            case 177: // HandleHistogramIntCount — schema + 1 value line
                {
                    // This is the hit count for a type/method profile site.
                    // Keep it if the paired histogram (195/196 at same ILOffset) has non-NULL handles.
                    // We peek ahead: if next schema is 195/196 at same offset with non-NULL handles, keep both.
                    // Otherwise defer — we'll handle it by pairing logic below.

                    // Always collect it; we'll decide to keep/drop when we see the paired 195/196.
                    string valueLine = lineIdx + 1 < lines.Length ? lines[lineIdx + 1] : "0";
                    current.PendingHistogramCount = (line, valueLine);
                    lineIdx += 2;
                    break;
                }

            case 195: // HandleHistogramTypes — schema + Count handle lines
            case 196: // HandleHistogramMethods — schema + Count handle lines
                {
                    // Read all handle lines — must keep ALL including all-NULL
                    // because the JIT reader expects exact record counts
                    List<string> handleLines = new(count);
                    bool hasNonNull = false;
                    for (int j = 1; j <= count && lineIdx + j < lines.Length; j++)
                    {
                        string handleLine = lines[lineIdx + j];
                        handleLines.Add(handleLine);
                        if (!handleLine.Contains("NULL"))
                            hasNonNull = true;
                    }

                    // Keep the paired 177 count entry if we have one pending
                    if (current.PendingHistogramCount is (string countSchema, string countValue))
                    {
                        current.Entries.Add(countSchema);
                        current.Entries.Add(countValue);
                        current.PendingHistogramCount = null;
                    }

                    // Keep this histogram (including all-NULL — required for correct parsing)
                    current.Entries.Add(line); // schema
                    foreach (string h in handleLines)
                        current.Entries.Add(h);

                    if (hasNonNull) histogramsKept++;
                    else histogramsAllNull++;

                    lineIdx += 1 + count;
                    break;
                }

            case 561: // GetLikelyClass — schema + 1 value line
            case 578: // GetLikelyMethod — schema + 1 value line
                {
                    current.Entries.Add(line);
                    if (lineIdx + 1 < lines.Length)
                        current.Entries.Add(lines[lineIdx + 1]);
                    lineIdx += 2;
                    break;
                }

            default:
                {
                    // Unknown kind — skip schema + count value lines
                    lineIdx += 1 + count;
                    break;
                }
        }

        continue;
    }

    // TypeHandle/MethodHandle outside a schema context (shouldn't happen, but skip)
    if (line.StartsWith("TypeHandle:") || line.StartsWith("MethodHandle:"))
    {
        lineIdx++;
        continue;
    }

    lineIdx++;
}

if (current is not null)
{
    FlushPending(current);
    methods.Add(current);
}

// Write output
using FileStream fs = File.Create(outputPath);
using GZipStream gz = new(fs, CompressionLevel.SmallestSize);
using StreamWriter writer = new(gz, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 65536);

writer.WriteLine($"*** START PGO Data, max index = {totalMethods} ***");

foreach (MethodRecord method in methods)
{
    bool keepByBlock = method.MaxBlockCount >= minBlock;
    bool keepByEdge = method.TotalEdgeCount >= minEdge;

    if (!keepByBlock && !keepByEdge)
        continue;

    if (method.Entries.Count == 0)
        continue;

    keptMethods++;

    // Count schema entries to rewrite the records field in the header
    int schemaCount = 0;
    foreach (string entry in method.Entries)
    {
        if (entry.StartsWith("Schema InstrumentationKind"))
            schemaCount++;
    }

    writer.WriteLine(RewriteRecordsCount(method.Header, schemaCount));
    if (method.MethodName is not null) writer.WriteLine(method.MethodName);
    if (method.Signature is not null) writer.WriteLine(method.Signature);

    foreach (string entry in method.Entries)
    {
        writer.WriteLine(entry);
        if (entry.StartsWith("Schema InstrumentationKind 385"))
            edgesKept++;
    }
}

writer.WriteLine("*** END PGO Data ***");
writer.Flush();
gz.Flush();

long inputSize = new FileInfo(inputPath).Length;
long outputSize = new FileInfo(outputPath).Length;

Console.WriteLine($"Input:      {inputPath} ({inputSize / 1024.0 / 1024.0:F1} MB)");
Console.WriteLine($"Output:     {outputPath} ({outputSize / 1024.0:F1} KB)");
Console.WriteLine($"Methods:    {keptMethods:N0} kept / {totalMethods:N0} total ({100.0 * keptMethods / totalMethods:F1}%)");
Console.WriteLine($"Edges:      {edgesKept:N0} block/edge entries");
Console.WriteLine($"Histograms: {histogramsKept + histogramsAllNull:N0} total ({histogramsKept:N0} with types, {histogramsAllNull:N0} all-NULL)");
Console.WriteLine($"Filters:    min-block={minBlock}, min-edge={minEdge}");
Console.WriteLine($"Ratio:      {100.0 * outputSize / inputSize:F2}% of original");

return 0;

static void FlushPending(MethodRecord method)
{
    if (method.PendingHistogramCount is (string cs, string cv))
    {
        method.Entries.Add(cs);
        method.Entries.Add(cv);
        method.PendingHistogramCount = null;
    }
}

static string RewriteRecordsCount(string header, int newCount)
{
    // "@@@ codehash 0x... methodhash 0x... ilSize 0x... records 0x0000002C"
    // Replace the records hex value with the new count
    int idx = header.IndexOf("records 0x", StringComparison.Ordinal);
    if (idx < 0) return header;
    int valueStart = idx + "records ".Length;
    return string.Concat(header.AsSpan(0, valueStart), $"0x{newCount:X8}");
}

static int ParseKind(string schemaLine)
{
    // "Schema InstrumentationKind NNN ..."
    int start = "Schema InstrumentationKind ".Length;
    int end = schemaLine.IndexOf(' ', start);
    return int.Parse(schemaLine.AsSpan(start, end - start));
}

static int ParseCount(string schemaLine)
{
    // "... Count NNN ..."
    int idx = schemaLine.IndexOf("Count ", StringComparison.Ordinal);
    if (idx < 0) return 0;
    idx += "Count ".Length;
    int end = schemaLine.IndexOf(' ', idx);
    if (end < 0) end = schemaLine.Length;
    return int.Parse(schemaLine.AsSpan(idx, end - idx));
}

class MethodRecord
{
    public string Header = "";
    public string? MethodName;
    public string? Signature;
    public List<string> Entries = [];
    public long MaxBlockCount;
    public long TotalEdgeCount;

    // Pending kind-177 entry waiting for its paired 195/196
    public (string Schema, string Value)? PendingHistogramCount;
}
