// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.TxPool;
using Nethermind.Xdc.TxPool;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.TxPool;

internal class Trc21TxPoolCostAndFundsProviderTests
{
    [Test]
    public void GetAdditionalFunds_ShouldReturnCapacity_ForTrc21Transaction()
    {
        UInt256 capacity = 123;

        Transaction tx = XdcTxPoolTestHelper.BuildTx(XdcTxPoolTestHelper.SenderAddress, XdcTxPoolTestHelper.TokenAddress);
        (Trc21TxPoolCostAndFundsProvider provider, XdcTxPoolTestHelper.FakeTrc21StateReader trc21Reader) = CreateProvider(
            headNumber: 100,
            isTipTrc21FeeEnabled: true,
            blockNumberGas50X: long.MaxValue);
        trc21Reader.FeeCapacities[XdcTxPoolTestHelper.TokenAddress] = capacity;

        UInt256 result = provider.GetAdditionalFunds(tx);

        Assert.That(result, Is.EqualTo(capacity));
    }

    [Test]
    public void GetAdditionalFunds_ShouldReturnZero_WhenFeatureIsDisabled()
    {
        Transaction tx = XdcTxPoolTestHelper.BuildTx(XdcTxPoolTestHelper.SenderAddress, XdcTxPoolTestHelper.TokenAddress);
        (Trc21TxPoolCostAndFundsProvider provider, XdcTxPoolTestHelper.FakeTrc21StateReader trc21Reader) = CreateProvider(
            headNumber: 100,
            isTipTrc21FeeEnabled: false,
            blockNumberGas50X: long.MaxValue);
        trc21Reader.FeeCapacities[XdcTxPoolTestHelper.TokenAddress] = 999;

        UInt256 result = provider.GetAdditionalFunds(tx);

        Assert.That(result, Is.EqualTo(UInt256.Zero));
    }

    [Test]
    public void TryGetTransactionCost_ShouldUseTrc21GasPrice_Before50x()
    {
        Transaction tx = XdcTxPoolTestHelper.BuildTx(XdcTxPoolTestHelper.SenderAddress, XdcTxPoolTestHelper.TokenAddress, gasLimit: 21_000, value: 7);
        (Trc21TxPoolCostAndFundsProvider provider, XdcTxPoolTestHelper.FakeTrc21StateReader trc21Reader) = CreateProvider(
            headNumber: 100,
            isTipTrc21FeeEnabled: true,
            blockNumberGas50X: 1_000);
        trc21Reader.FeeCapacities[XdcTxPoolTestHelper.TokenAddress] = 1;

        bool success = provider.TryGetTransactionCost(tx, out UInt256 txCost);
        UInt256 expected = (UInt256)XdcConstants.Trc21GasPrice * (UInt256)tx.GasLimit + tx.ValueRef;

        Assert.That(success, Is.True);
        Assert.That(txCost, Is.EqualTo(expected));
    }

    [Test]
    public void TryGetTransactionCost_ShouldUseTrc21GasPrice50x_After50x()
    {
        Transaction tx = XdcTxPoolTestHelper.BuildTx(XdcTxPoolTestHelper.SenderAddress, XdcTxPoolTestHelper.TokenAddress, gasLimit: 21_000, value: 7);
        (Trc21TxPoolCostAndFundsProvider provider, XdcTxPoolTestHelper.FakeTrc21StateReader trc21Reader) = CreateProvider(
            headNumber: 100,
            isTipTrc21FeeEnabled: true,
            blockNumberGas50X: 10);
        trc21Reader.FeeCapacities[XdcTxPoolTestHelper.TokenAddress] = 1;

        bool success = provider.TryGetTransactionCost(tx, out UInt256 txCost);
        UInt256 expected = (UInt256)XdcConstants.Trc21GasPrice50x * (UInt256)tx.GasLimit + tx.ValueRef;

        Assert.That(success, Is.True);
        Assert.That(txCost, Is.EqualTo(expected));
    }

    [Test]
    public void TryGetTransactionCost_ShouldFallbackToDefault_ForNonTrc21Transaction()
    {
        Transaction tx = XdcTxPoolTestHelper.BuildTx(XdcTxPoolTestHelper.SenderAddress, XdcTxPoolTestHelper.RecipientAddress, gasPrice: 11, gasLimit: 21_000, value: 3);
        (Trc21TxPoolCostAndFundsProvider provider, _) = CreateProvider(
            headNumber: 100,
            isTipTrc21FeeEnabled: true,
            blockNumberGas50X: 1);

        bool success = provider.TryGetTransactionCost(tx, out UInt256 txCost);
        bool expectedSuccess = DefaultTxPoolCostAndFundsProvider.Instance.TryGetTransactionCost(tx, out UInt256 expectedTxCost);

        Assert.That(success, Is.EqualTo(expectedSuccess));
        Assert.That(txCost, Is.EqualTo(expectedTxCost));
    }

    private static (Trc21TxPoolCostAndFundsProvider, XdcTxPoolTestHelper.FakeTrc21StateReader) CreateProvider(
        long headNumber,
        bool isTipTrc21FeeEnabled,
        long blockNumberGas50X)
    {
        (IBlockTree blockTree, ISpecProvider specProvider) = XdcTxPoolTestHelper.Create(
            headNumber,
            isTipTrc21FeeEnabled,
            blockNumberGas50X);

        XdcTxPoolTestHelper.FakeTrc21StateReader trc21Reader = new();
        return (new Trc21TxPoolCostAndFundsProvider(blockTree, specProvider, trc21Reader), trc21Reader);
    }
}
