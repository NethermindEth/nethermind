// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

/// <summary>
/// Reads a .callgraph file (callee_ip caller_ip pairs from SpgoExtractor) and
/// resolves IPs to method names using the .etlx MethodMemoryMap. Outputs a
/// CallChainProfile JSON file for crossgen2's --callchain-profile / CallFrequency layout.
///
/// Also collects per-method size data from the .etlx for potential CDS (Cache-Directed Sort)
/// implementation.
///
/// JSON format expected by crossgen2's CallChainProfile:
/// {
///   "CallerMethodName": [["CalleeA", "CalleeB"], [100, 50]],
///   ...
/// }
/// </summary>
static class CallChainGenerator
{
    public static int Generate(string etlxPath, string outputJsonPath)
    {
        string callGraphPath = Path.ChangeExtension(etlxPath, ".callgraph");
        if (!File.Exists(callGraphPath))
        {
            Console.Error.WriteLine($"No .callgraph file found at: {callGraphPath}");
            return 1;
        }

        if (!File.Exists(etlxPath))
        {
            Console.Error.WriteLine($"No .etlx file found at: {etlxPath}");
            return 1;
        }

        Console.WriteLine($"Loading method map from {etlxPath}...");

        // Build IP -> method name map from .etlx CLR events
        Dictionary<string, MethodInfo> methodsByName = new();
        Dictionary<ulong, string> ipToMethod = new();

        using (TraceLog traceLog = TraceLog.OpenOrConvert(etlxPath, new TraceLogOptions { KeepAllEvents = true }))
        {
            foreach (TraceProcess process in traceLog.Processes)
            {
                // Build from MethodLoadVerbose events (have native start address + size)
                foreach (MethodLoadUnloadVerboseTraceData evt in
                    process.EventsInProcess.ByEventType<MethodLoadUnloadVerboseTraceData>())
                {
                    string name = FormatMethodName(evt);
                    ulong start = evt.MethodStartAddress;
                    int size = evt.MethodSize;

                    if (start == 0 || size == 0)
                        continue;

                    // Register all IPs in this method's range
                    // We only need the start address for lookup - use range check later
                    if (!methodsByName.ContainsKey(name))
                    {
                        methodsByName[name] = new MethodInfo(name, start, size);
                    }

                    ipToMethod[start] = name;
                }
            }
        }

        Console.WriteLine($"  {methodsByName.Count:N0} methods resolved from .etlx");

        // Build sorted array for binary search IP resolution
        List<(ulong start, int size, string name)> sortedMethods = new();
        foreach (MethodInfo mi in methodsByName.Values)
        {
            sortedMethods.Add((mi.Start, mi.Size, mi.Name));
        }
        sortedMethods.Sort((a, b) => a.start.CompareTo(b.start));

        // Parse .callgraph and aggregate caller -> callee -> count
        Dictionary<string, Dictionary<string, int>> callGraph = new();
        Dictionary<string, int> exclusiveSamples = new();
        int resolvedEdges = 0;
        int unresolvedEdges = 0;
        int totalLines = 0;

        Console.WriteLine($"Processing {callGraphPath}...");

        foreach (string line in File.ReadLines(callGraphPath))
        {
            ReadOnlySpan<char> span = line.AsSpan().Trim();
            if (span.IsEmpty || span[0] == '#')
                continue;

            totalLines++;
            int space = span.IndexOf(' ');
            if (space <= 0)
                continue;

            if (!ulong.TryParse(span.Slice(0, space), NumberStyles.HexNumber, null, out ulong calleeIp))
                continue;
            if (!ulong.TryParse(span.Slice(space + 1), NumberStyles.HexNumber, null, out ulong callerIp))
                continue;

            string? calleeName = ResolveIp(sortedMethods, calleeIp);
            string? callerName = ResolveIp(sortedMethods, callerIp);

            // Count exclusive samples for callee
            if (calleeName != null)
            {
                if (exclusiveSamples.TryGetValue(calleeName, out int count))
                    exclusiveSamples[calleeName] = count + 1;
                else
                    exclusiveSamples[calleeName] = 1;
            }

            if (callerName != null && calleeName != null && callerName != calleeName)
            {
                if (!callGraph.TryGetValue(callerName, out Dictionary<string, int>? callees))
                {
                    callees = new Dictionary<string, int>();
                    callGraph[callerName] = callees;
                }
                if (callees.TryGetValue(calleeName, out int edgeCount))
                    callees[calleeName] = edgeCount + 1;
                else
                    callees[calleeName] = 1;

                resolvedEdges++;
            }
            else
            {
                unresolvedEdges++;
            }
        }

        Console.WriteLine($"  {totalLines:N0} lines, {resolvedEdges:N0} resolved edges, {unresolvedEdges:N0} unresolved");
        Console.WriteLine($"  {callGraph.Count:N0} unique callers, {exclusiveSamples.Count:N0} methods with samples");

        // Write CallChainProfile JSON
        Console.WriteLine($"Writing {outputJsonPath}...");
        using (FileStream fs = File.Create(outputJsonPath))
        using (Utf8JsonWriter writer = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();

            foreach (KeyValuePair<string, Dictionary<string, int>> caller in callGraph)
            {
                writer.WritePropertyName(caller.Key);
                writer.WriteStartArray();

                // First array: callee names
                writer.WriteStartArray();
                foreach (KeyValuePair<string, int> callee in caller.Value)
                {
                    writer.WriteStringValue(callee.Key);
                }
                writer.WriteEndArray();

                // Second array: call counts
                writer.WriteStartArray();
                foreach (KeyValuePair<string, int> callee in caller.Value)
                {
                    writer.WriteNumberValue(callee.Value);
                }
                writer.WriteEndArray();

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        // Also write a method-sizes file for potential CDS implementation
        string sizesPath = Path.ChangeExtension(outputJsonPath, ".sizes");
        int sizesWritten = 0;
        using (StreamWriter sw = new StreamWriter(sizesPath))
        {
            sw.WriteLine("# method_name native_size_bytes exclusive_samples");
            foreach (MethodInfo mi in methodsByName.Values)
            {
                int samples = exclusiveSamples.GetValueOrDefault(mi.Name, 0);
                sw.WriteLine($"{mi.Name}\t{mi.Size}\t{samples}");
                sizesWritten++;
            }
        }

        Console.WriteLine($"  CallChain JSON: {new FileInfo(outputJsonPath).Length:N0} bytes");
        Console.WriteLine($"  Method sizes: {sizesPath} ({sizesWritten:N0} methods)");

        return resolvedEdges > 0 ? 0 : 1;
    }

    static string? ResolveIp(List<(ulong start, int size, string name)> sortedMethods, ulong ip)
    {
        // Binary search for the method containing this IP
        int lo = 0;
        int hi = sortedMethods.Count - 1;

        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            ulong methodStart = sortedMethods[mid].start;

            if (ip < methodStart)
            {
                hi = mid - 1;
            }
            else if (ip >= methodStart + (ulong)sortedMethods[mid].size)
            {
                lo = mid + 1;
            }
            else
            {
                return sortedMethods[mid].name;
            }
        }

        return null;
    }

    static string FormatMethodName(MethodLoadUnloadVerboseTraceData evt)
    {
        // Format: Namespace.Type.Method(ArgTypes)
        // crossgen2's CallChainProfile resolves by matching against MethodDesc.ToString()
        string ns = evt.MethodNamespace;
        string name = evt.MethodName;
        string sig = evt.MethodSignature;

        if (!string.IsNullOrEmpty(ns))
            return $"{ns}.{name}({sig})";
        return $"{name}({sig})";
    }

    record MethodInfo(string Name, ulong Start, int Size);
}
