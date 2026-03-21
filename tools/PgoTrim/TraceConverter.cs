// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Reflection;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

/// <summary>
/// Converts a perfcollect .trace.zip to .etlx with the missing MethodDetails CTF mapping.
/// Uses the same CreateFromLinuxEventSources path as TraceLog.CreateFromLttngTextDataFile
/// but with the MethodDetails mapping injected first.
/// </summary>
static class TraceConverter
{
    public static int Convert(string inputPath, string outputPath)
    {
        if (!inputPath.EndsWith(".trace.zip", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Input must be a .trace.zip file: {inputPath}");
            return 1;
        }

        string etlxPath = outputPath.EndsWith(".etlx", StringComparison.OrdinalIgnoreCase)
            ? outputPath
            : Path.ChangeExtension(outputPath, ".etlx");

        var teVersion = typeof(TraceLog).Assembly.GetName().Version;
        Console.WriteLine($"TraceEvent version: {teVersion}");
        Console.WriteLine($"Converting {inputPath} to {etlxPath}...");

        using (var ctfSource = new CtfTraceEventSource(inputPath))
        {
            // Register CLR parser (populates _eventMapping with known mappings)
            new ClrTraceEventParser(ctfSource);

            // Inject missing CTF mappings
            // CtfEventMapping(eventName, providerGuid, opcode, id, version)
            InjectCtfMapping(ctfSource, "DotNETRuntime:MethodDetails",
                ClrTraceEventParser.ProviderGuid, opcode: 43, id: 72, version: 0);
            // Use version=0 for the V1 event — TraceLog's template lookup matches
            // on (providerGuid, eventId, opcode) and the only registered template
            // for ID 190 is version 0. Version 1 events won't match if we use version=1.
            InjectCtfMapping(ctfSource, "DotNETRuntime:MethodILToNativeMap_V1",
                ClrTraceEventParser.ProviderGuid, opcode: 87, id: 190, version: 0);

            // Debug: verify mappings are in the dictionary
            var debugField = typeof(CtfTraceEventSource).GetField("_eventMapping",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (debugField?.GetValue(ctfSource) is System.Collections.IDictionary debugDict)
            {
                int ilMapCount = 0;
                foreach (var key in debugDict.Keys)
                    if (key.ToString()!.Contains("ILToNativeMap"))
                    {
                        Console.WriteLine($"  Mapping: {key}");
                        ilMapCount++;
                    }
                Console.WriteLine($"  Total mappings: {debugDict.Count}, ILToNativeMap entries: {ilMapCount}");
            }

            // Debug: count events seen by the CTF source
            int ctfTotal = 0, ctfILMap = 0;
            ctfSource.AllEvents += evt => {
                ctfTotal++;
                if (evt.EventName?.Contains("ILToNative") == true) ctfILMap++;
            };

            // Use the same path as CreateFromLttngTextDataFile:
            // CreateFromLinuxEventSources(source, etlxPath, options)
            // This is an internal method — call via reflection
            Console.WriteLine("Processing events via CreateFromLinuxEventSources...");
            var method = typeof(TraceLog).GetMethod("CreateFromLinuxEventSources",
                BindingFlags.NonPublic | BindingFlags.Static);

            if (method != null)
            {
                method.Invoke(null, [ctfSource, etlxPath, new TraceLogOptions { KeepAllEvents = true }]);
            }
            else
            {
                Console.Error.WriteLine("Error: Could not find CreateFromLinuxEventSources");
                return 1;
            }
            Console.WriteLine($"CTF source dispatched: {ctfTotal:N0} events, {ctfILMap:N0} ILToNativeMap");
        }

        // Verify the .etlx has the events dotnet-pgo needs
        Console.WriteLine("Verifying via OpenOrConvert (same path as dotnet-pgo)...");
        using var traceLog = TraceLog.OpenOrConvert(etlxPath, new TraceLogOptions { KeepAllEvents = true });
        int totalMethodDetails = 0;
        int totalMethodLoad = 0;
        int totalJitStart = 0;
        int totalILToNativeMap = 0;

        foreach (var process in traceLog.Processes)
        {
            int md = process.EventsInProcess.ByEventType<MethodDetailsTraceData>().Count();
            int ml = process.EventsInProcess.ByEventType<MethodLoadUnloadVerboseTraceData>().Count();
            int js = process.EventsInProcess.ByEventType<MethodJittingStartedTraceData>().Count();
            int il = process.EventsInProcess.ByEventType<MethodILToNativeMapTraceData>().Count();
            totalMethodDetails += md;
            totalMethodLoad += ml;
            totalJitStart += js;
            totalILToNativeMap += il;

            if (md > 0 || ml > 0)
            {
                Console.WriteLine($"  {process.Name} (PID {process.ProcessID}): " +
                                  $"MethodDetails={md:N0} MethodLoadVerbose={ml:N0} JittingStarted={js:N0} ILToNativeMap={il:N0}");
            }
        }

        // Debug: count raw events with ID 190 (MethodILToNativeMap)
        int rawId190 = 0;
        int etlxTotal = 0;
        foreach (var process in traceLog.Processes)
            foreach (var evt in process.EventsInProcess)
            {
                etlxTotal++;
                if ((int)evt.ID == 190) rawId190++;
            }
        Console.WriteLine($"  .etlx total events: {etlxTotal:N0}");

        Console.WriteLine($"Total: MethodDetails={totalMethodDetails:N0} " +
                          $"MethodLoadVerbose={totalMethodLoad:N0} JittingStarted={totalJitStart:N0} " +
                          $"ILToNativeMap={totalILToNativeMap:N0} (raw ID 190: {rawId190:N0})");
        Console.WriteLine($"Output: {etlxPath} ({new FileInfo(etlxPath).Length / 1024.0 / 1024.0:F1} MB)");

        return totalMethodDetails > 0 ? 0 : 1;
    }

    static void InjectCtfMapping(CtfTraceEventSource source, string eventName,
        Guid providerGuid, int opcode, int id, int version)
    {
        var field = typeof(CtfTraceEventSource).GetField("_eventMapping",
            BindingFlags.NonPublic | BindingFlags.Instance);

        if (field?.GetValue(source) is not IDictionary dict)
        {
            Console.Error.WriteLine("Warning: Could not access _eventMapping");
            return;
        }

        var mappingType = typeof(CtfTraceEventSource).Assembly.GetTypes()
            .FirstOrDefault(t => t.Name == "CtfEventMapping");
        var ctor = mappingType?.GetConstructors().FirstOrDefault();

        if (ctor == null)
        {
            Console.Error.WriteLine("Warning: Could not find CtfEventMapping constructor");
            return;
        }

        dict[eventName] = ctor.Invoke([eventName, providerGuid, opcode, id, version]);
        Console.WriteLine($"Injected: {eventName} -> opcode={opcode}, id={id}");
    }
}
