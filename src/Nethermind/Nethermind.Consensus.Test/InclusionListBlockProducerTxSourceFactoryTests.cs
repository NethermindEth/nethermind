// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class InclusionListBlockProducerTxSourceFactoryTests
{
    [Test]
    public void IL_transactions_precede_mempool_transactions_in_pipeline()
    {
        Transaction mempoolTx = Build.A.Transaction
            .WithNonce(7)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;
        Transaction ilTx = Build.A.Transaction
            .WithNonce(3)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        ITxSource mempoolSource = Substitute.For<ITxSource>();
        mempoolSource.GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<ulong>(), Arg.Any<PayloadAttributes>(), Arg.Any<bool>())
            .Returns([mempoolTx]);

        IBlockProducerTxSourceFactory baseFactory = Substitute.For<IBlockProducerTxSourceFactory>();
        baseFactory.Create().Returns(mempoolSource);

        InclusionListTxSource il = new(
            new EthereumEcdsa(MainnetSpecProvider.Instance.ChainId),
            new CustomSpecProvider(((ForkActivation)0, Bogota.Instance)),
            LimboLogs.Instance);
        byte[][] ilBytes = [TxDecoder.Instance.Encode(ilTx, RlpBehaviors.SkipTypedWrapping).Bytes];
        il.Set(ilBytes, Bogota.Instance);
        // IL is keyed by the build's PayloadAttributes array.
        PayloadAttributes payloadAttributes = new() { InclusionListTransactions = ilBytes };

        ITxSource txSource = new InclusionListBlockProducerTxSourceFactory(baseFactory, il).Create();

        BlockHeader parent = Build.A.BlockHeader.TestObject;
        List<Transaction> selectedTxs = [.. txSource.GetTransactions(parent, 30_000_000UL, payloadAttributes)];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selectedTxs, Has.Count.GreaterThanOrEqualTo(2));
            Assert.That(selectedTxs[0].Nonce, Is.EqualTo(3UL), "the IL transaction must be selected before the mempool source");
            Assert.That(selectedTxs.Any(t => t.Nonce == 7UL), Is.True, "the mempool transaction must still appear after the IL");
        }

    }

    [Test]
    public void Empty_IL_still_drains_mempool()
    {
        Transaction mempoolTx = Build.A.Transaction
            .WithNonce(7)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;

        ITxSource mempoolSource = Substitute.For<ITxSource>();
        mempoolSource.GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<ulong>(), Arg.Any<PayloadAttributes>(), Arg.Any<bool>())
            .Returns([mempoolTx]);

        IBlockProducerTxSourceFactory baseFactory = Substitute.For<IBlockProducerTxSourceFactory>();
        baseFactory.Create().Returns(mempoolSource);

        InclusionListTxSource il = new(
            new EthereumEcdsa(MainnetSpecProvider.Instance.ChainId),
            new CustomSpecProvider(((ForkActivation)0, Bogota.Instance)),
            LimboLogs.Instance);
        // IL is empty

        ITxSource txSource = new InclusionListBlockProducerTxSourceFactory(baseFactory, il).Create();

        BlockHeader parent = Build.A.BlockHeader.TestObject;
        List<Transaction> selectedTxs = [.. txSource.GetTransactions(parent, 30_000_000UL)];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selectedTxs, Has.Count.EqualTo(1));
            Assert.That(selectedTxs[0].Nonce, Is.EqualTo(7UL));
        }
    }
}
