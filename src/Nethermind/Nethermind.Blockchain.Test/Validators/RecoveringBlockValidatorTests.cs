// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators;

[Parallelizable(ParallelScope.All)]
public class RecoveringBlockValidatorTests
{
    private static (RecoveringBlockValidator sut, IBlockValidator inner, Block block) Setup()
    {
        ISpecProvider specProvider = MainnetSpecProvider.Instance;
        IEthereumEcdsa ecdsa = new EthereumEcdsa(specProvider.ChainId);
        Transaction tx = Build.A.Transaction.SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
        tx.SenderAddress = null; // mimic an ingress block whose senders aren't recovered yet
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ParisBlockNumber).WithTransactions(tx).TestObject;

        IBlockValidator inner = Substitute.For<IBlockValidator>();
        RecoveringBlockValidator sut = new(inner, ecdsa, specProvider, LimboLogs.Instance);
        return (sut, inner, block);
    }

    [Test]
    public void ValidateSuggestedBlock_recovers_sender_then_delegates()
    {
        (RecoveringBlockValidator sut, IBlockValidator inner, Block block) = Setup();
        BlockHeader parent = Build.A.BlockHeader.TestObject;

        sut.ValidateSuggestedBlock(block, parent, out _);

        Assert.That(block.Transactions[0].SenderAddress, Is.EqualTo(TestItem.AddressA));
        inner.Received(1).ValidateSuggestedBlock(block, parent, out Arg.Any<string?>(), Arg.Any<bool>());
    }

    [Test]
    public void ValidateOrphanedBlock_recovers_sender_then_delegates()
    {
        (RecoveringBlockValidator sut, IBlockValidator inner, Block block) = Setup();

        sut.ValidateOrphanedBlock(block, out _);

        Assert.That(block.Transactions[0].SenderAddress, Is.EqualTo(TestItem.AddressA));
        inner.Received(1).ValidateOrphanedBlock(block, out Arg.Any<string?>());
    }

    [Test]
    public void ValidateProcessedBlock_delegates_without_recovering()
    {
        (RecoveringBlockValidator sut, IBlockValidator inner, Block block) = Setup();
        TxReceipt[] receipts = [];

        sut.ValidateProcessedBlock(block, receipts, block, out _);

        Assert.That(block.Transactions[0].SenderAddress, Is.Null);
        inner.Received(1).ValidateProcessedBlock(block, receipts, block, out Arg.Any<string?>());
    }
}
