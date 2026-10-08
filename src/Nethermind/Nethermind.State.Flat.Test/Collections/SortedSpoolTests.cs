// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State.Flat.Collections;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Collections;

public class SortedSpoolTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp() => _directory = Directory.CreateTempSubdirectory().FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    /// <summary>A fan-in or pre-merge threshold below the run count forces intermediate merge rounds.</summary>
    /// <remarks>The writer count exercises the concurrent path: every key must still surface exactly once,
    /// however the partitioned producers happened to spread it across runs.</remarks>
    [Test]
    public void Spool_merges_runs_in_key_order_collapsing_duplicates(
        [Values(2, 3, 128)] int maxFanIn, [Values(2, 64)] int preMergeThreshold, [Values(1, 4)] int writerCount,
        [Values(1, 2)] int maxConcurrentPreMerges)
    {
        // A buffer of a few records per run, so a few hundred records spill into many runs.
        int finalMerges = 0;
        using SortedSpool spool = new(_directory, 512, writerCount, LimboLogs.Instance, CancellationToken.None)
        {
            MaxFanIn = maxFanIn,
            PreMergeThreshold = preMergeThreshold,
            MaxConcurrentPreMerges = maxConcurrentPreMerges,
            FinalMerge = () => finalMerges++
        };
        SortedDictionary<ValueHash256, byte[]> expected = [];
        for (int index = 0; index < 400; index++)
        {
            ValueHash256 key = ValueKeccak.Compute(BitConverter.GetBytes(index % 250));
            expected[key] = ValueKeccak.Compute(key.Bytes).Bytes.ToArray();
        }

        // Every key past 250 repeats an earlier one with the same value, so it must collapse.
        Parallel.For(0, writerCount, new ParallelOptions { MaxDegreeOfParallelism = writerCount }, worker =>
        {
            using SortedSpool.Writer writer = spool.CreateWriter();
            for (int index = worker; index < 400; index += writerCount)
            {
                ValueHash256 key = ValueKeccak.Compute(BitConverter.GetBytes(index % 250));
                writer.Add(key.Bytes, ValueKeccak.Compute(key.Bytes).Bytes);
            }
        });

        // Read twice: the runs outlive the merge, so a second cursor must replay the same sequence.
        for (int pass = 0; pass < 2; pass++)
        {
            using SortedSpool.Cursor cursor = spool.Read();
            using IEnumerator<KeyValuePair<ValueHash256, byte[]>> reference = expected.GetEnumerator();
            while (cursor.MoveNext())
            {
                Assert.That(reference.MoveNext(), Is.True, "more merged records than distinct keys");
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(cursor.Key.ToArray(), Is.EqualTo(reference.Current.Key.Bytes.ToArray()), "key");
                    Assert.That(cursor.Value.ToArray(), Is.EqualTo(reference.Current.Value), "value");
                }
            }
            Assert.That(reference.MoveNext(), Is.False, "fewer merged records than distinct keys");
        }
        Assert.That(finalMerges, Is.EqualTo(1), "final merge hook");
    }
}
