// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Evm.State;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class ReadOnlyTxProcessingScopeTests
{
    [Test]
    public void Test_WhenDispose_ThenStateRootWillReset()
    {
        bool closed = false;
        IDisposable closer = new Reactive.AnonymousDisposable(() => closed = true);
        ReadOnlyTxProcessingScope env = new ReadOnlyTxProcessingScope(
            Substitute.For<ITransactionProcessor>(),
            closer,
            Substitute.For<IWorldState>());

        env.Dispose();

        closed.Should().BeTrue();
    }

    [Test]
    public async Task AddressWarmerWait_WhenWarmupDoesNotFinish_WarnsWithoutReportingCompletion()
    {
        using ManualResetEventSlim doneEvent = new(initialState: false);
        TestLogger logger = new();
        Block block = Build.A.Block.WithNumber(123).TestObject;

        Task waitTask = Task.Run(() => BlockCachePreWarmer.WaitForAddressWarmer(doneEvent, TimeSpan.FromMilliseconds(100), block, new ILogger(logger)));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        while (!logger.LogList.Exists(log => log.Contains("Waiting for address warmer to finish", StringComparison.Ordinal)))
        {
            await Task.Delay(10, cts.Token);
        }
        waitTask.IsCompleted.Should().BeFalse("the prewarmer must not report completion while the address warmer is still running");

        doneEvent.Set();
        await waitTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
