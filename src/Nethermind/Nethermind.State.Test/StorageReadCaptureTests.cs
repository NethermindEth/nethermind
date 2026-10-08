// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Store.Test;

[Parallelizable(ParallelScope.All)]
public class StorageReadCaptureTests
{
    /// <summary>Captures on several threads draw on one budget: together they record exactly as many cells as it held.</summary>
    [Test]
    public void Captures_sharing_a_budget_record_no_more_cells_than_it_holds()
    {
        const int budget = 64, captures = 8, cellsPerCapture = 50;
        PreBlockCaches caches = new(TestPreBlockCachesConfig.Small);
        StrongBox<int> remaining = new(budget);
        int[] recorded = new int[captures];
        using Barrier start = new(captures);

        Thread[] threads = Enumerable.Range(0, captures).Select(i => new Thread(() =>
        {
            using PreBlockCaches.StorageReadCapture capture = caches.BeginStorageReadCapture(remaining);
            start.SignalAndWait();
            for (int j = 0; j < cellsPerCapture; j++)
            {
                capture.Record(new StorageCell(TestItem.Addresses[i], (UInt256)j));
            }

            recorded[i] = capture.Cells.Count;
        })).ToArray();
        foreach (Thread thread in threads) thread.Start();
        foreach (Thread thread in threads) thread.Join();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorded.Sum(), Is.EqualTo(budget));
            Assert.That(remaining.Value, Is.Zero);
        }
    }

    [Test]
    public void Recording_a_cell_again_claims_no_second_credit()
    {
        PreBlockCaches caches = new(TestPreBlockCachesConfig.Small);
        StrongBox<int> remaining = new(4);
        using PreBlockCaches.StorageReadCapture capture = caches.BeginStorageReadCapture(remaining);
        StorageCell cell = new(TestItem.AddressA, 1);

        capture.Record(cell);
        capture.Record(cell);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(capture.Cells.Count, Is.EqualTo(1));
            Assert.That(remaining.Value, Is.EqualTo(3));
        }
    }
}
