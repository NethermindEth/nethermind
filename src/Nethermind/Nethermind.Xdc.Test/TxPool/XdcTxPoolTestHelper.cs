// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Spec;
using NSubstitute;
using System;
using System.Collections.Generic;

namespace Nethermind.Xdc.Test.TxPool;

internal static class XdcTxPoolTestHelper
{
    public static readonly Address SenderAddress = new("0x00000000000000000000000000000000000000f1");
    public static readonly Address RecipientAddress = new("0x00000000000000000000000000000000000000b2");
    public static readonly Address TokenAddress = new("0x00000000000000000000000000000000000000a1");
    public static readonly Address SignerAddress = new("0x00000000000000000000000000000000000000f0");
    public static readonly Address RandomizeContract = new("0x0000000000000000000000000000000000000090");

    private static readonly byte[] DefaultTransferData = [0xa9, 0x05, 0x9c, 0xbb];

    public static (IBlockTree blockTree, ISpecProvider specProvider) Create(
        long headNumber,
        bool isTipTrc21FeeEnabled,
        long blockNumberGas50X)
    {
        XdcBlockHeader headHeader = Build.A.XdcBlockHeader().WithNumber(headNumber).TestObject;
        Block headBlock = Build.A.Block.WithHeader(headHeader).TestObject;

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(headBlock);

        IXdcReleaseSpec spec = Substitute.For<IXdcReleaseSpec>();
        spec.IsTipTrc21FeeEnabled.Returns(isTipTrc21FeeEnabled);
        spec.EpochLength.Returns(900);
        spec.BlockNumberGas50x.Returns(blockNumberGas50X);
        spec.BlockSignerContract.Returns(new Address("0x0000000000000000000000000000000000000089"));
        spec.RandomizeSMCBinary.Returns(RandomizeContract);

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);
        specProvider.GetFinalSpec().Returns(spec);

        return (blockTree, specProvider);
    }

    public static Transaction BuildTx(
        Address sender,
        Address to,
        long gasPrice = XdcConstants.Trc21GasPrice,
        long gasLimit = 21_000,
        ulong value = 0,
        UInt256? nonce = null)
    {
        TransactionBuilder<Transaction> txBuilder = Build.A.Transaction
            .WithSenderAddress(sender)
            .WithTo(to)
            .WithGasPrice((UInt256)gasPrice)
            .WithGasLimit(gasLimit)
            .WithValue(value)
            .WithData(DefaultTransferData);

        if (nonce is not null)
            txBuilder.WithNonce(nonce.Value);

        return txBuilder.TestObject;
    }

    internal sealed class FakeTrc21StateReader : ITrc21StateReader
    {
        public Dictionary<Address, UInt256> FeeCapacities { get; } = [];
        public bool IsValid { get; set; } = true;
        public int ValidateCalls { get; private set; }

        public IReadOnlyDictionary<Address, UInt256> GetFeeCapacities(XdcBlockHeader? baseBlock) => new Dictionary<Address, UInt256>(FeeCapacities);

        public bool ValidateTransaction(XdcBlockHeader? baseBlock, Address from, Address token, ReadOnlySpan<byte> data)
        {
            ValidateCalls++;
            return IsValid;
        }
    }
}
