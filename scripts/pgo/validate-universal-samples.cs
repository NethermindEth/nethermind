// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:property ManagePackageVersionsCentrally=false
#:property PublishAot=false
#:package Microsoft.Diagnostics.Tracing.TraceEvent@3.2.8

using Microsoft.Diagnostics.Tracing;
using System.Globalization;
using System.Text.Json;

if (args.Length != 5)
{
    Console.Error.WriteLine("Usage: validate-universal-samples.cs <raw.nettrace> <pid> <before-ms> <after-ms> <report.json>");
    return 2;
}

int processId = int.Parse(args[1], CultureInfo.InvariantCulture);
double before = double.Parse(args[2], CultureInfo.InvariantCulture);
double after = double.Parse(args[3], CultureInfo.InvariantCulture);
if (!double.IsFinite(before) || !double.IsFinite(after) || before > after)
    throw new ArgumentException("Sample window must have finite, ordered endpoints");

long records = 0;
long weight = 0;
Dictionary<string, long> eventKinds = [];
using EventPipeEventSource source = new(args[0]);
source.Dynamic.All += e =>
{
    if (e.ProcessID != processId || e.ProviderName != "Universal.Events")
        return;
    eventKinds[e.EventName] = checked(eventKinds.GetValueOrDefault(e.EventName) + 1);
    if (e.EventName != "cpu" || e.TimeStampRelativeMSec < before || e.TimeStampRelativeMSec > after)
        return;
    ulong value = Convert.ToUInt64(e.PayloadByName("Value"), CultureInfo.InvariantCulture);
    weight = checked(weight + checked((long)value));
    records = checked(records + 1);
};
source.Process();
File.WriteAllText(args[4], JsonSerializer.Serialize(new { processId, before, after, records, weight, eventKinds },
    new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Raw Universal CPU records: {records}; sample weight: {weight}");
return 0;
