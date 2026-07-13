// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Runner.Monitoring;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Monitoring;

public class DataFeedTests
{
    [Test]
    public async Task Does_not_prepare_system_stats_without_subscribers()
    {
        using CancellationTokenSource lifetime = new();
        lifetime.Cancel();

        IMainProcessingContext mainProcessingContext = Substitute.For<IMainProcessingContext>();
        mainProcessingContext.BlockchainProcessor.Returns(Substitute.For<IBlockchainProcessor>());

        DataFeed dataFeed = new(
            Substitute.For<ITxPool>(),
            Substitute.For<ISpecProvider>(),
            Substitute.For<IReceiptFinder>(),
            Substitute.For<IBlockTree>(),
            Substitute.For<ISyncPeerPool>(),
            mainProcessingContext,
            LimboLogs.Instance,
            lifetime.Token);

        byte[] data = await dataFeed.GetStatsTask(delayMs: 0);

        Assert.That(data, Is.Null);
    }
}
