// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.TxPool;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.TxPool;

internal class XdcIncomingTxFilterTests
{
    [Test]
    public void Accept_ShouldRejectTrc21Transfer_WhenReaderValidationFails()
    {
        Address sender = new("0x00000000000000000000000000000000000000f1");
        Address token = new("0x00000000000000000000000000000000000000a1");

        Transaction tx = BuildTx(sender, token);
        (XdcIncomingTxFilter sut, FakeTrc21StateReader trc21Reader) = CreateSut(100, true);
        trc21Reader.FeeCapacities[token] = 1;
        trc21Reader.IsValid = false;

        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>());
        AcceptTxResult result = sut.Accept(tx, ref state, TxHandlingOptions.None);

        Assert.That(result, Is.EqualTo(AcceptTxResult.InsufficientFunds));
        Assert.That(trc21Reader.ValidateCalls, Is.EqualTo(1));
    }

    [Test]
    public void Accept_ShouldSkipTrc21Validation_WhenRecipientIsNotTrc21Token()
    {
        Address sender = new("0x00000000000000000000000000000000000000f1");
        Address recipient = new("0x00000000000000000000000000000000000000b2");

        Transaction tx = BuildTx(sender, recipient);
        (XdcIncomingTxFilter sut, FakeTrc21StateReader trc21Reader) = CreateSut(100, true);

        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>());
        AcceptTxResult result = sut.Accept(tx, ref state, TxHandlingOptions.None);

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
        Assert.That(trc21Reader.ValidateCalls, Is.EqualTo(0));
    }

    [Test]
    public void Accept_ShouldSkipTrc21Validation_WhenFeatureIsDisabled()
    {
        Address sender = new("0x00000000000000000000000000000000000000f1");
        Address token = new("0x00000000000000000000000000000000000000a1");

        Transaction tx = BuildTx(sender, token);
        (XdcIncomingTxFilter sut, FakeTrc21StateReader trc21Reader) = CreateSut(100, false);
        trc21Reader.FeeCapacities[token] = 1;
        trc21Reader.IsValid = false;

        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>());
        AcceptTxResult result = sut.Accept(tx, ref state, TxHandlingOptions.None);

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
        Assert.That(trc21Reader.ValidateCalls, Is.EqualTo(0));
    }

    private static (XdcIncomingTxFilter, FakeTrc21StateReader) CreateSut(long headNumber, bool isTipTrc21FeeEnabled)
    {
        XdcBlockHeader headHeader = (XdcBlockHeader)Build.A.XdcBlockHeader().WithNumber(headNumber).TestObject;
        Block headBlock = Build.A.Block.WithHeader(headHeader).TestObject;

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(headBlock);

        ISigner signer = Substitute.For<ISigner>();
        signer.Address.Returns(new Address("0x00000000000000000000000000000000000000f0"));

        IXdcReleaseSpec spec = Substitute.For<IXdcReleaseSpec>();
        spec.IsTipTrc21FeeEnabled.Returns(isTipTrc21FeeEnabled);
        spec.EpochLength.Returns(900);
        spec.BlockSignerContract.Returns(new Address("0x0000000000000000000000000000000000000089"));
        spec.RandomizeSMCBinary.Returns(new Address("0x0000000000000000000000000000000000000090"));

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        FakeTrc21StateReader trc21Reader = new();
        return (new XdcIncomingTxFilter(signer, blockTree, specProvider, trc21Reader), trc21Reader);
    }

    private static Transaction BuildTx(Address sender, Address to)
    {
        return Build.A.Transaction
            .WithSenderAddress(sender)
            .WithTo(to)
            .WithGasPrice(1)
            .WithGasLimit(21_000)
            .WithData(new byte[] { 0xa9, 0x05, 0x9c, 0xbb })
            .TestObject;
    }

    private sealed class FakeTrc21StateReader : ITrc21StateReader
    {
        public Dictionary<Address, UInt256> FeeCapacities { get; } = [];
        public bool IsValid { get; set; } = true;
        public int ValidateCalls { get; private set; }

        public Dictionary<Address, UInt256> GetFeeCapacities(XdcBlockHeader? baseBlock) => new(FeeCapacities);

        public bool ValidateTransaction(XdcBlockHeader? baseBlock, Address from, Address token, ReadOnlySpan<byte> data)
        {
            ValidateCalls++;
            return IsValid;
        }
    }
}
