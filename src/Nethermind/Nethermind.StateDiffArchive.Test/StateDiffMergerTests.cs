// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.StateDiffArchive.Merging;
using Nethermind.StateDiffArchive.Storage;
using NUnit.Framework;

namespace Nethermind.StateDiffArchive.Test;

[Parallelizable(ParallelScope.Self)]
public class StateDiffMergerTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp() => _root = Path.Combine(Path.GetTempPath(), $"sds-merge-{Guid.NewGuid():N}");

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string WriteSource(string name, params (ulong block, byte[] data)[] entries)
    {
        string dir = Path.Combine(_root, name);
        using SlotStore store = new(dir, "diff");
        foreach ((ulong block, byte[] data) in entries) store.Write(block, data);
        return dir;
    }

    private static byte[]? Read(SlotStore store, ulong block)
    {
        byte[]? result = null;
        store.TryRead(block, (data, _) => result = data.ToArray(), 0);
        return result;
    }

    [Test]
    public void Merges_disjoint_ranges_including_a_shared_boundary_era()
    {
        // Two sources, disjoint blocks, but blocks 5 and 6 share era file 0 (the boundary case).
        Dictionary<ulong, byte[]> expected = new()
        {
            [0] = Bytes.FromHexString("0xa0"),
            [1] = Bytes.FromHexString("0xa1"),
            [5] = Bytes.FromHexString("0xa5"),
            [6] = Bytes.FromHexString("0xb6"),
            [SlotFile.SlotsPerFile] = Bytes.FromHexString("0xb7"),
            [SlotFile.SlotsPerFile + 1] = Bytes.FromHexString("0xb8"),
        };

        string source1 = WriteSource("s1", (0, expected[0]), (1, expected[1]), (5, expected[5]));
        string source2 = WriteSource("s2", (6, expected[6]),
            (SlotFile.SlotsPerFile, expected[SlotFile.SlotsPerFile]),
            (SlotFile.SlotsPerFile + 1, expected[SlotFile.SlotsPerFile + 1]));

        string output = Path.Combine(_root, "merged");
        StateDiffMerger.MergeResult result = StateDiffMerger.Merge([source1, source2], output, LimboLogs.Instance.GetClassLogger<StateDiffMergerTests>());

        using SlotStore merged = new(output, "diff");
        Assert.Multiple(() =>
        {
            foreach ((ulong block, byte[] data) in expected)
                Assert.That(Read(merged, block), Is.EqualTo(data), $"merged block {block}");

            Assert.That(result.BlocksMerged, Is.EqualTo(expected.Count));
            Assert.That(result.FirstBlock, Is.EqualTo(0));
            Assert.That(result.LastBlock, Is.EqualTo(SlotFile.SlotsPerFile + 1));
        });
    }

    [Test]
    public void Reports_contiguity_for_adjacent_ranges()
    {
        string source1 = WriteSource("c1", (0, [1]), (1, [2]), (2, [3]));
        string source2 = WriteSource("c2", (3, [4]), (4, [5]), (5, [6]));

        string output = Path.Combine(_root, "merged-contig");
        StateDiffMerger.MergeResult result = StateDiffMerger.Merge([source1, source2], output, LimboLogs.Instance.GetClassLogger<StateDiffMergerTests>());

        Assert.Multiple(() =>
        {
            Assert.That(result.BlocksMerged, Is.EqualTo(6));
            Assert.That(result.Gaps, Is.EqualTo(0));
            Assert.That(result.Contiguous, Is.True);
        });
    }
}
