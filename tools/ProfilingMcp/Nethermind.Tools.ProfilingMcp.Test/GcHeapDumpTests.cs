// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Nethermind.Tools.ProfilingMcp.Memory;
using NUnit.Framework;

namespace Nethermind.Tools.ProfilingMcp.Test;

public class GcHeapDumpTests
{
    private const int Instances = 1000;

    private static readonly List<Retained> RetainedByStatic = [];

    [Test]
    [CancelAfter(120_000)]
    public async Task Reads_live_objects_and_retention_paths_of_own_process(CancellationToken cancellationToken)
    {
        Allocate();
        try
        {
            GcHeapDumpResult dump = await GcHeapDump.CollectAsync(Environment.ProcessId, withReferences: true, cancellationToken);
            // The runtime names a nested type without its declaring type, but generic arguments with it.
            RetentionReport report = RetentionAnalyzer.Analyze(dump.Graph!, new Regex($"^{nameof(Retained)}$"), maxPaths: 5, maxVisited: long.MaxValue);
            Dictionary<string, TypeStat> types = dump.Snapshot.Types.ToDictionary(static t => t.Name);
            string list = $"System.Collections.Generic.List<{typeof(Retained).FullName}>";

            using (Assert.EnterMultipleScope())
            {
                Assert.That(dump.EventsLost, Is.Zero);
                Assert.That(dump.PauseMs, Is.GreaterThan(0));
                Assert.That(types[nameof(Retained)].Count, Is.EqualTo(Instances));
                Assert.That(types.GetValueOrDefault(nameof(Garbage))?.Count ?? 0, Is.Zero, "only objects surviving the induced GC are reported");
                Assert.That(types["System.Byte[]"].LargeObjectBytes, Is.GreaterThanOrEqualTo(100_000));
                Assert.That(types, Does.ContainKey(list));

                Assert.That(report.InstanceCount, Is.EqualTo(Instances));
                Assert.That(report.Referrers[0], Is.EqualTo(new KeyValuePair<string, long>($"{nameof(Retained)}[]", Instances)));
                Assert.That(report.Paths, Has.Count.EqualTo(1));
                Assert.That((report.Paths[0].Instances, report.Paths[0].Bytes), Is.EqualTo((Instances, types[nameof(Retained)].Bytes)));
                Assert.That(report.Paths[0].Steps.Select(static s => s.TypeName), Does.Contain(list));
                Assert.That(report.Paths[0].Steps[^1].Field, Is.EqualTo("[]"));
            }
        }
        finally
        {
            RetainedByStatic.Clear();
        }
    }

    [TestCase("System.Collections.Generic.Dictionary`2[System.String,System.Int32]", ExpectedResult = "System.Collections.Generic.Dictionary<System.String, System.Int32>")]
    [TestCase("Entry[System.String,System.Collections.Generic.List`1[System.Int32[]]][]", ExpectedResult = "Entry<System.String, System.Collections.Generic.List<System.Int32[]>>[]")]
    [TestCase("System.Byte[,]", ExpectedResult = "System.Byte[,]")]
    [TestCase("System.String", ExpectedResult = "System.String")]
    public string Formats_runtime_type_names_like_clrmd(string name) => GcHeapDump.FormatTypeName(name);

    /// <remarks>Allocating in a separate frame leaves no reference to the last object in a stack slot of the test.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Allocate()
    {
        for (int i = 0; i < Instances; i++)
            RetainedByStatic.Add(new Retained(new byte[i == 0 ? 100_000 : 10]));
        for (int i = 0; i < Instances; i++)
            _ = new Garbage();
    }

    private sealed record Retained(byte[] Payload);

    private sealed class Garbage;
}
