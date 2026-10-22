// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Shutter.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
class ShutterTxSourceTests
{
    [Test]
    public void Load_transactions_does_not_deadlock_with_concurrent_cancel()
    {
        const int waiterCount = 1000;
        Random rnd = new(ShutterTestsCommon.Seed);
        Timestamper timestamper = ShutterTestsCommon.InitTimestamper(ShutterTestsCommon.InitialSlotTimestamp, 0);
        ShutterTxSource txSource = ShutterTestsCommon.InitApi(rnd, timestamper).TxSource;

        using CancellationTokenSource source = new();
        Task[] waitTasks = Enumerable.Range(0, waiterCount)
            .Select(_ => txSource.WaitForTransactions(ShutterTestsCommon.InitialSlot, source.Token))
            .ToArray();

        // Registered last, so it runs first (LIFO) and holds the cancel thread until LoadTransactions has started
        // completing waiters; the tx source's own callbacks then run while it is still disposing registrations.
        using ManualResetEventSlim cancelStarted = new();
        source.Token.Register(() =>
        {
            cancelStarted.Set();
            SpinWait.SpinUntil(() => waitTasks[0].IsCompleted, TimeSpan.FromSeconds(10));
        });
        Task.Run(source.Cancel);
        cancelStarted.Wait();

        IShutterKeyValidator.ValidatedKeys keys = new() { Slot = ShutterTestsCommon.InitialSlot, Keys = new([], 0) };
        Task load = Task.Run(() => txSource.LoadTransactions(null, Build.A.BlockHeader.TestObject, keys));

        Assert.That(load.Wait(TimeSpan.FromSeconds(10)), Is.True, "LoadTransactions deadlocked against the cancel callback");
        Assert.That(waitTasks, Is.All.Matches<Task>(static t => t is { IsCompletedSuccessfully: true }));
    }

    // A token cancelled just before the wait is registered runs the cancellation callback inline, which then found no
    // wait to cancel and left the stored one pending forever.
    [Test]
    public void Wait_for_transactions_with_an_already_cancelled_token_ends_cancelled()
    {
        Random rnd = new(ShutterTestsCommon.Seed);
        Timestamper timestamper = ShutterTestsCommon.InitTimestamper(ShutterTestsCommon.InitialSlotTimestamp, 0);
        ShutterTxSource txSource = ShutterTestsCommon.InitApi(rnd, timestamper).TxSource;

        Task wait = txSource.WaitForTransactions(ShutterTestsCommon.InitialSlot, new CancellationToken(canceled: true));

        Assert.That(() => wait.Wait(TimeSpan.FromSeconds(5)), Throws.InstanceOf<AggregateException>()
            .With.InnerException.InstanceOf<OperationCanceledException>());
    }
}
