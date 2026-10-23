// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Threading;
using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

// From verifier xs2v (23 Oct 2026): pins what the existing stamp tests leave open, the own/nested split of the
// processing thread's loads and where its join waits start and end.
public partial class FlatWorldStateScopeProviderTests
{
    private const int Xs2vSleepMs = 50;

    [Test]
    public void StorageRootStamps_Xs2v_OwnReadsAndWaitsFromTheLastOwnItem()
    {
        BlockDiag diag = new() { BlockNumber = 1 };
        BlockDiag? outer = BlockDiag.Current;
        BlockDiag.Current = diag;
        try
        {
            using (BlockDiag.EnterStorageItem(diag, 0, firstLoop: true))
            {
                // A loop whose one item runs on this thread: its load is the item's own, its wait starts at the item's end.
                BlockDiag.NestedLoop loop = BlockDiag.BeginNestedLoop(BlockDiag.NestedKind.BulkSet);
                using (loop.RunItem())
                {
                    BlockDiag.TrieReadEnd(BlockDiag.TrieReadStart());
                    Thread.Sleep(Xs2vSleepMs);
                }

                loop.End();

                // An inner loop ended Xs2vSleepMs after its own item, inside an item of an outer loop: that wait counts.
                BlockDiag.NestedLoop outerLoop = BlockDiag.BeginNestedLoop(BlockDiag.NestedKind.Hash);
                using (outerLoop.RunItem())
                {
                    BlockDiag.NestedLoop innerLoop = BlockDiag.BeginNestedLoop(BlockDiag.NestedKind.BulkSet);
                    using (innerLoop.RunItem()) { }
                    Thread.Sleep(Xs2vSleepMs);
                    innerLoop.End();
                }

                outerLoop.End();

                // A loop this thread runs no item of, begun Xs2vSleepMs after the last item end: its wait is its own length.
                Thread.Sleep(Xs2vSleepMs);
                BlockDiag.NestedLoop idle = BlockDiag.BeginNestedLoop(BlockDiag.NestedKind.BulkSet);
                idle.End();
            }
        }
        finally
        {
            BlockDiag.Current = outer;
        }

        double waitMs = diag.Get(BlockDiag.F.SrNestedWait).WallTicks * 1000.0 / Stopwatch.Frequency;
        NUnit.Framework.TestContext.Out.WriteLine($"xs2v: wait {waitMs:F1} ms, own {diag.Get(BlockDiag.C.SrItem0ReadsOwn)}, nested {diag.Get(BlockDiag.C.SrItem0ReadsNested)}");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(diag.Get(BlockDiag.C.SrItem0ReadsOwn), Is.EqualTo(1), "a load in an item the processing thread runs is its own");
            Assert.That(diag.Get(BlockDiag.C.SrItem0ReadsNested), Is.Zero, "no helper ran an item");
            Assert.That(waitMs, Is.GreaterThanOrEqualTo(Xs2vSleepMs - 5), "the inner loop's wait counts");
            Assert.That(waitMs, Is.LessThan(2 * Xs2vSleepMs - 5), "waits start at the thread's last item of the same loop");
        }
    }
}
