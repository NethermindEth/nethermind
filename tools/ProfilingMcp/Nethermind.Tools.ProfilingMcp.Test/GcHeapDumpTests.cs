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

    [TestCase(2, false, "may still be reachable")]
    [TestCase(long.MaxValue, true, null)]
    public void Claims_unreachable_only_after_a_complete_search(long maxVisited, bool expectPath, string? expectedNote)
    {
        // root -> Holder -> Middle -> Retained
        HeapGraph graph = new(
            typeNames: ["Holder", "Middle", nameof(Retained)],
            nodeTypes: [0, 1, 2],
            addresses: [0x100, 0x200, 0x300],
            sizes: [24, 24, 24],
            edgeStarts: [0, 1, 2, 2],
            edgeTargets: [0x200, 0x300],
            roots: [(0x100UL, "static")],
            dependentHandles: []);

        RetentionReport report = RetentionAnalyzer.Analyze(graph, new Regex($"^{nameof(Retained)}$"), maxPaths: 5, maxVisited);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.Paths, expectPath ? Has.Count.EqualTo(1) : Is.Empty);
            if (expectedNote is not null)
            {
                Assert.That(report.SearchComplete, Is.False);
                Assert.That(MemoryTools.NoPathsNote(report.SearchComplete, maxVisited), Does.Contain(expectedNote).And.Not.Contain("unreachable"));
            }
        }
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task Overlapping_reads_of_one_process_both_see_the_whole_heap(CancellationToken cancellationToken)
    {
        Allocate();
        try
        {
            Task<GcHeapDumpResult> first = GcHeapDump.CollectAsync(Environment.ProcessId, withReferences: false, cancellationToken);
            Task<GcHeapDumpResult> second = GcHeapDump.CollectAsync(Environment.ProcessId, withReferences: false, cancellationToken);
            GcHeapDumpResult[] results = await Task.WhenAll(first, second);

            Assert.That(results.Select(static r => r.Snapshot.Types.FirstOrDefault(static t => t.Name == nameof(Retained))?.Count), Is.All.EqualTo(Instances));
        }
        finally
        {
            RetainedByStatic.Clear();
        }
    }

    [Test]
    public void Finds_matching_objects_held_through_other_matching_objects()
    {
        // root -> Retained -> Retained -> Retained
        HeapGraph graph = new(
            typeNames: [nameof(Retained)],
            nodeTypes: [0, 0, 0],
            addresses: [0x100, 0x200, 0x300],
            sizes: [24, 24, 24],
            edgeStarts: [0, 1, 2, 2],
            edgeTargets: [0x200, 0x300],
            roots: [(0x100UL, "static")],
            dependentHandles: []);

        RetentionReport report = RetentionAnalyzer.Analyze(graph, new Regex($"^{nameof(Retained)}$"), maxPaths: 5, maxVisited: long.MaxValue);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.SearchComplete, Is.True);
            Assert.That(report.Paths.Sum(static p => p.Instances), Is.EqualTo(report.InstanceCount));
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
