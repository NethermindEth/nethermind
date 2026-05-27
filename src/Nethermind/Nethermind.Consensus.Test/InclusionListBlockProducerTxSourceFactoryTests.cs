// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
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
        // FOCIL (EIP-7805): if a block fills from the mempool alone it would trivially
        // satisfy the IL via gas exhaustion — defeating censorship-resistance.
        // The factory must prepend IL so the IL drains first.
        Transaction mempoolTx = Build.A.Transaction
            .WithNonce(7)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;
        Transaction ilTx = Build.A.Transaction
            .WithNonce(3)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        ITxSource mempoolSource = Substitute.For<ITxSource>();
        mempoolSource.GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<long>(), Arg.Any<PayloadAttributes>(), Arg.Any<bool>())
            .Returns([mempoolTx]);

        IBlockProducerTxSourceFactory baseFactory = Substitute.For<IBlockProducerTxSourceFactory>();
        baseFactory.Create().Returns(mempoolSource);

        InclusionListTxSource il = new(
            new EthereumEcdsa(MainnetSpecProvider.Instance.ChainId),
            new CustomSpecProvider(((ForkActivation)0, Bogota.Instance)),
            LimboLogs.Instance);
        il.Set([Serialization.Rlp.TxDecoder.Instance.Encode(ilTx, Serialization.Rlp.RlpBehaviors.SkipTypedWrapping).Bytes], Bogota.Instance);

        InclusionListBlockProducerTxSourceFactory subject = new(baseFactory, il);
        ITxSource composed = subject.Create();

        BlockHeader parent = Build.A.BlockHeader.TestObject;
        List<Transaction> drained = composed.GetTransactions(parent, 30_000_000).ToList();

        drained.Should().HaveCountGreaterOrEqualTo(2);
        drained[0].Nonce.Should().Be((UInt256)3, "the IL transaction must be drained before the mempool source");
        drained.Any(t => t.Nonce == (UInt256)7).Should().BeTrue("the mempool transaction must still appear after the IL");
    }

    [Test]
    public void Empty_IL_still_drains_mempool()
    {
        Transaction mempoolTx = Build.A.Transaction
            .WithNonce(7)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;

        ITxSource mempoolSource = Substitute.For<ITxSource>();
        mempoolSource.GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<long>(), Arg.Any<PayloadAttributes>(), Arg.Any<bool>())
            .Returns([mempoolTx]);

        IBlockProducerTxSourceFactory baseFactory = Substitute.For<IBlockProducerTxSourceFactory>();
        baseFactory.Create().Returns(mempoolSource);

        InclusionListTxSource il = new(
            new EthereumEcdsa(MainnetSpecProvider.Instance.ChainId),
            new CustomSpecProvider(((ForkActivation)0, Bogota.Instance)),
            LimboLogs.Instance);
        // No .Set() call — IL is empty.

        InclusionListBlockProducerTxSourceFactory subject = new(baseFactory, il);
        ITxSource composed = subject.Create();

        BlockHeader parent = Build.A.BlockHeader.TestObject;
        List<Transaction> drained = composed.GetTransactions(parent, 30_000_000).ToList();

        drained.Should().ContainSingle();
        drained[0].Nonce.Should().Be((UInt256)7);
    }
}
