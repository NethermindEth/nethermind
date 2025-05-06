// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class OptimismReceiptTests
{
    [SetUp]
    public void SetUp()
    {
        TxDecoder.Instance.RegisterDecoder(new OptimismTxDecoder<Transaction>());
    }

    [Test]
    public void ContainsOperatorFeeParameters_PreIsthmus_IsNull()
    {
        Block block = Build.A.Block
            .WithHeader(Build.A.BlockHeader.TestObject)
            .WithTransactions(
                Build.A.Transaction
                    .WithType(TxType.DepositTx)
                    .TestObject
            )
            .TestObject;
        Transaction tx = Build.A.Transaction.TestObject;

        var specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).IsEip1559Enabled.Returns(true);
        var helper = Substitute.For<IOptimismSpecHelper>();
        helper.IsIsthmus(Arg.Any<BlockHeader>()).Returns(false);

        var blockGasInfo = new L1BlockGasInfo(block, helper);
        var receipt = new OptimismReceiptForRpc(
            tx.Hash!,
            new OptimismTxReceipt(),
            tx.GetGasInfo(specProvider.GetSpec(block.Header), block.Header),
            blockGasInfo.GetTxGasInfo(tx)
        );

        receipt.OperatorFeeScalar.Should().Be(null);
        receipt.OperatorFeeConstant.Should().Be(null);
    }

    [Test]
    public void NoOperatorFeeParameters_DirectlyPostIsthmus_FromExtraData()
    {
        // Ecotone style l1 attributes, directly post Isthmus with:
        // - baseFeeScalar = 2
        // - blobBaseFeeScalar = 3
        // - baseFee = 1000*1e6
        // - blobBaseFee = 10*1e6
        var l1Attributes = Bytes.FromHexString("098999be000000020000000300000000000004d200000000000004d200000000000004d2000000000000000000000000000000000000000000000000000000003b9aca00000000000000000000000000000000000000000000000000000000000098968000000000000000000000000000000000000000000000000000000000000004d200000000000000000000000000000000000000000000000000000000000004d2");

        Block block = Build.A.Block
            .WithHeader(Build.A.BlockHeader.TestObject)
            .WithTransactions(
                Build.A.Transaction
                    .WithType(TxType.DepositTx)
                    .WithData(l1Attributes)
                    .TestObject
                )
            .TestObject;
        Transaction tx = Build.A.Transaction.TestObject;

        var specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).IsEip1559Enabled.Returns(true);
        var helper = Substitute.For<IOptimismSpecHelper>();
        helper.IsIsthmus(Arg.Any<BlockHeader>()).Returns(true);

        var blockGasInfo = new L1BlockGasInfo(block, helper);
        var receipt = new OptimismReceiptForRpc(
            tx.Hash!,
            new OptimismTxReceipt(),
            tx.GetGasInfo(specProvider.GetSpec(block.Header), block.Header),
            blockGasInfo.GetTxGasInfo(tx)
        );

        receipt.L1BaseFeeScalar.Should().Be((UInt256)2);
        receipt.L1BlobBaseFeeScalar.Should().Be((UInt256)3);
        receipt.L1GasPrice.Should().Be((UInt256)(1000 * 1e6));
        receipt.L1BlobBaseFee.Should().Be((UInt256)(10 * 1e6));
        receipt.OperatorFeeScalar.Should().Be((UInt256)0);
        receipt.OperatorFeeConstant.Should().Be((UInt256)0);
    }

    [Test]
    public void ContainsOperatorFeeParameters_PostIsthmus_FromExtraData()
    {
        // Isthmus style l1 attributes with:
        // - baseFeeScalar = 2
        // - blobBaseFeeScalar = 3
        // - baseFee = 1000*1e6
        // - blobBaseFee = 10*1e6
        // - operatorFeeScalar = 7
        // - operatorFeeConstant = 9
        var l1Attributes = Bytes.FromHexString("098999be000000020000000300000000000004d200000000000004d200000000000004d2000000000000000000000000000000000000000000000000000000003b9aca00000000000000000000000000000000000000000000000000000000000098968000000000000000000000000000000000000000000000000000000000000004d200000000000000000000000000000000000000000000000000000000000004d2000000070000000000000009");

        Block block = Build.A.Block
            .WithHeader(Build.A.BlockHeader.TestObject)
            .WithTransactions(
                Build.A.Transaction
                    .WithType(TxType.DepositTx)
                    .WithData(l1Attributes)
                    .TestObject
                )
            .TestObject;
        Transaction tx = Build.A.Transaction.TestObject;

        var specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).IsEip1559Enabled.Returns(true);
        var helper = Substitute.For<IOptimismSpecHelper>();
        helper.IsIsthmus(Arg.Any<BlockHeader>()).Returns(true);

        var blockGasInfo = new L1BlockGasInfo(block, helper);
        var receipt = new OptimismReceiptForRpc(
            tx.Hash!,
            new OptimismTxReceipt(),
            tx.GetGasInfo(specProvider.GetSpec(block.Header), block.Header),
            blockGasInfo.GetTxGasInfo(tx)
        );

        receipt.L1BaseFeeScalar.Should().Be((UInt256)2);
        receipt.L1BlobBaseFeeScalar.Should().Be((UInt256)3);
        receipt.L1GasPrice.Should().Be((UInt256)(1000 * 1e6));
        receipt.L1BlobBaseFee.Should().Be((UInt256)(10 * 1e6));
        receipt.OperatorFeeScalar.Should().Be((UInt256)7);
        receipt.OperatorFeeConstant.Should().Be((UInt256)9);
    }
}
