// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Threading;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Threading;

/// <remarks>
/// Locks the two guest-only folds that let every <c>Metrics.Increment*</c> in Trie, Trie.Pruning,
/// Db and Evm compile away. Flipping either one silently reinstates the counters, and flipping
/// <see cref="ProcessingThread.IsBlockProcessingThread"/> to <c>true</c> would also switch on the
/// work callers gate behind it, such as <c>BlockProcessor.SetAccountChanges</c>.
/// </remarks>
public class ZkEvmMetricPrimitivesTests
{
    [Test]
    public void Block_processing_thread_flag_stays_false_when_set()
    {
        ProcessingThread.IsBlockProcessingThread = true;

        Assert.That(ProcessingThread.IsBlockProcessingThread, Is.False);
    }

    [Test]
    public void Striped_long_stays_empty_when_incremented()
    {
        StripedLong counter = new();

        counter.Increment();
        counter.Add(41);

        Assert.That(counter.Sum, Is.Zero);
    }
}
