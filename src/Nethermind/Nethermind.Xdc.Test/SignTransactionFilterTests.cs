// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.TxPool;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[Parallelizable(ParallelScope.All)]
internal class SignTransactionFilterTests
{
    private static readonly Address BlockSignerContract = TestItem.AddressA;
    private static readonly Address RandomizeSMC = TestItem.AddressB;

    private static SignTransactionFilter CreateFilter(Block? head, Snapshot? snapshot)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(head);

        IXdcReleaseSpec xdcSpec = Substitute.For<IXdcReleaseSpec>();
        xdcSpec.BlockSignerContract.Returns(BlockSignerContract);
        xdcSpec.RandomizeSMCBinary.Returns(RandomizeSMC);
        xdcSpec.EpochLength.Returns(900);

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        snapshotManager.GetSnapshotByBlockNumber(Arg.Any<long>(), Arg.Any<IXdcReleaseSpec>()).Returns(snapshot);

        return new SignTransactionFilter(snapshotManager, blockTree, specProvider);
    }

    private static Block HeadBlock(long number = 100) =>
        Build.A.Block.WithHeader(Build.A.XdcBlockHeader().WithNumber(number).TestObject).TestObject;

    [Test]
    public void Accept_NullHead_ReturnsSyncing()
    {
        SignTransactionFilter filter = CreateFilter(head: null, snapshot: null);
        Transaction tx = Build.A.Transaction.WithTo(TestItem.AddressC).TestObject;
        TxFilteringState state = default;

        filter.Accept(tx, ref state, TxHandlingOptions.None).Should().Be(AcceptTxResult.Syncing);
    }

    [Test]
    public void Accept_NonSpecialTx_ReturnsAcceptedWithoutServiceFlag()
    {
        SignTransactionFilter filter = CreateFilter(HeadBlock(), snapshot: null);
        Transaction tx = Build.A.Transaction.WithTo(TestItem.AddressC).TestObject;
        TxFilteringState state = default;

        filter.Accept(tx, ref state, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
        tx.IsServiceTransaction.Should().BeFalse();
    }

    [Test]
    public void Accept_SpecialTxFromCandidate_ReturnsAcceptedAndSetsServiceFlag()
    {
        Address candidate = TestItem.AddressD;
        Snapshot snapshot = new(101, Hash256.Zero, [candidate]);
        SignTransactionFilter filter = CreateFilter(HeadBlock(100), snapshot);

        Transaction tx = SignTransactionManager.CreateTxSign(99, Hash256.Zero, 0, BlockSignerContract, candidate);
        TxFilteringState state = default;

        filter.Accept(tx, ref state, TxHandlingOptions.None).Should().Be(AcceptTxResult.Accepted);
        tx.IsServiceTransaction.Should().BeTrue();
    }

    [Test]
    public void Accept_SpecialTxFromNonCandidate_ReturnsInvalid()
    {
        Snapshot snapshot = new(101, Hash256.Zero, [TestItem.AddressD]);
        SignTransactionFilter filter = CreateFilter(HeadBlock(100), snapshot);

        Transaction tx = SignTransactionManager.CreateTxSign(99, Hash256.Zero, 0, BlockSignerContract, TestItem.AddressE);
        TxFilteringState state = default;

        filter.Accept(tx, ref state, TxHandlingOptions.None).Should().Be(AcceptTxResult.Invalid);
    }

    [Test]
    public void Accept_SpecialTxNullSnapshot_ReturnsInvalid()
    {
        SignTransactionFilter filter = CreateFilter(HeadBlock(100), snapshot: null);

        Transaction tx = SignTransactionManager.CreateTxSign(99, Hash256.Zero, 0, BlockSignerContract, TestItem.AddressD);
        TxFilteringState state = default;

        filter.Accept(tx, ref state, TxHandlingOptions.None).Should().Be(AcceptTxResult.Invalid);
    }

    [Test]
    public void CreateTxSign_HashCalculatedAfterSigning_CoversSignature()
    {
        Transaction tx = SignTransactionManager.CreateTxSign(100, Hash256.Zero, 0, BlockSignerContract, TestItem.AddressB);

        Hash256 hashBeforeSigning = tx.CalculateHash();

        Signer signer = new(0, TestItem.PrivateKeyB, NullLogManager.Instance);
        signer.Sign(tx).GetAwaiter().GetResult();

        Hash256 hashAfterSigning = tx.CalculateHash();

        hashAfterSigning.Should().NotBe(hashBeforeSigning, "hash must cover the signature");
    }
}
