// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    public void SetUp() =>
        TxDecoder.Instance.RegisterDecoder(new OptimismTxDecoder<Transaction>());

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

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).IsEip1559Enabled.Returns(true);
        IOptimismSpecHelper helper = Substitute.For<IOptimismSpecHelper>();
        helper.IsIsthmus(Arg.Any<BlockHeader>()).Returns(false);

        L1BlockGasInfo blockGasInfo = new(block, helper);
        OptimismReceiptForRpc receipt = new(
            tx.Hash!,
            new OptimismTxReceipt(),
            0,
            tx.GetGasInfo(specProvider.GetSpec(block.Header), block.Header),
            blockGasInfo.GetTxGasInfo(tx)
        );

        Assert.That(receipt.OperatorFeeScalar, Is.EqualTo(null));
        Assert.That(receipt.OperatorFeeConstant, Is.EqualTo(null));
    }

    [Test]
    public void NoOperatorFeeParameters_DirectlyPostIsthmus_FromExtraData()
    {
        // Ecotone style l1 attributes, directly post Isthmus with:
        // - baseFeeScalar = 2
        // - blobBaseFeeScalar = 3
        // - baseFee = 1000*1e6
        // - blobBaseFee = 10*1e6
        byte[] l1Attributes = Bytes.FromHexString("098999be000000020000000300000000000004d200000000000004d200000000000004d2000000000000000000000000000000000000000000000000000000003b9aca00000000000000000000000000000000000000000000000000000000000098968000000000000000000000000000000000000000000000000000000000000004d200000000000000000000000000000000000000000000000000000000000004d2");

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

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).IsEip1559Enabled.Returns(true);
        IOptimismSpecHelper helper = Substitute.For<IOptimismSpecHelper>();
        helper.IsIsthmus(Arg.Any<BlockHeader>()).Returns(true);

        L1BlockGasInfo blockGasInfo = new(block, helper);
        OptimismReceiptForRpc receipt = new(
            tx.Hash!,
            new OptimismTxReceipt(),
            0,
            tx.GetGasInfo(specProvider.GetSpec(block.Header), block.Header),
            blockGasInfo.GetTxGasInfo(tx)
        );

        AssertL1AndOperatorFees(receipt, expectedOperatorFeeScalar: 0, expectedOperatorFeeConstant: 0);
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
        byte[] l1Attributes = Bytes.FromHexString("098999be000000020000000300000000000004d200000000000004d200000000000004d2000000000000000000000000000000000000000000000000000000003b9aca00000000000000000000000000000000000000000000000000000000000098968000000000000000000000000000000000000000000000000000000000000004d200000000000000000000000000000000000000000000000000000000000004d2000000070000000000000009");

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

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).IsEip1559Enabled.Returns(true);
        IOptimismSpecHelper helper = Substitute.For<IOptimismSpecHelper>();
        helper.IsIsthmus(Arg.Any<BlockHeader>()).Returns(true);

        L1BlockGasInfo blockGasInfo = new(block, helper);
        OptimismReceiptForRpc receipt = new(
            tx.Hash!,
            new OptimismTxReceipt(),
            0,
            tx.GetGasInfo(specProvider.GetSpec(block.Header), block.Header),
            blockGasInfo.GetTxGasInfo(tx)
        );

        AssertL1AndOperatorFees(receipt, expectedOperatorFeeScalar: 7, expectedOperatorFeeConstant: 9);
    }

    private static void AssertL1AndOperatorFees(OptimismReceiptForRpc receipt, UInt256 expectedOperatorFeeScalar, UInt256 expectedOperatorFeeConstant)
    {
        using (Assert.EnterMultipleScope())
        {
            // L1 attribute fields are identical across the Ecotone-postIsthmus and Isthmus payloads — only the operator-fee tail differs
            Assert.That(receipt.L1BaseFeeScalar, Is.EqualTo((UInt256)2));
            Assert.That(receipt.L1BlobBaseFeeScalar, Is.EqualTo((UInt256)3));
            Assert.That(receipt.L1GasPrice, Is.EqualTo((UInt256)(1000 * 1e6)));
            Assert.That(receipt.L1BlobBaseFee, Is.EqualTo((UInt256)(10 * 1e6)));
            Assert.That(receipt.OperatorFeeScalar, Is.EqualTo(expectedOperatorFeeScalar));
            Assert.That(receipt.OperatorFeeConstant, Is.EqualTo(expectedOperatorFeeConstant));
        }
    }
}
