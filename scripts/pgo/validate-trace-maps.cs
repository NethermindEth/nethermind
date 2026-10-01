// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:property ManagePackageVersionsCentrally=false
#:property PublishAot=false
#:package Microsoft.Diagnostics.Tracing.TraceEvent@3.2.8

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using System.Collections;
using System.Text.Json;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: validate-trace-maps.cs <raw.nettrace> <converted.etlx> <report.json>");
    return 2;
}

List<string> raw = [];
using (EventPipeEventSource source = new(args[0]))
{
    source.Clr.MethodILToNativeMap += e => raw.Add(Snapshot(e));
    source.Process();
}

List<string> retained = [];
using (TraceLog trace = new(args[1]))
{
    foreach (MethodILToNativeMapTraceData e in trace.Events.ByEventType<MethodILToNativeMapTraceData>())
    {
        retained.Add(Snapshot(e));
    }
}
raw.Sort(StringComparer.Ordinal);
retained.Sort(StringComparer.Ordinal);
bool valid = raw.Count > 0 && raw.SequenceEqual(retained);
File.WriteAllText(args[2], JsonSerializer.Serialize(new { valid, rawCount = raw.Count,
    retainedCount = retained.Count, raw, retained }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"IL/native maps: raw={raw.Count}, retained={retained.Count}, identical={valid}");
return valid ? 0 : 1;

static string Snapshot(MethodILToNativeMapTraceData e)
{
    Dictionary<string, object?> payload = [];
    foreach (string name in e.PayloadNames)
    {
        object? value = e.PayloadByName(name);
        payload[name] = value is IEnumerable entries && value is not string ? entries.Cast<object>().ToArray() : value;
    }
    return JsonSerializer.Serialize(new { e.ProcessID, e.ThreadID, e.Version,
        timeMicroseconds = Math.Round(e.TimeStampRelativeMSec * 1000), payload });
}
