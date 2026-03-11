// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.TxPool;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.TxPool;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.TxPool;

internal class XdcIncomingTxFilterTests
{
    [Test]
    public void Accept_ShouldRejectTrc21Transfer_WhenReaderValidationFails()
    {
        Transaction tx = XdcTxPoolTestHelper.BuildTx(XdcTxPoolTestHelper.SenderAddress, XdcTxPoolTestHelper.TokenAddress);
        (XdcIncomingTxFilter filter, XdcTxPoolTestHelper.FakeTrc21StateReader trc21Reader) = CreateFilter(100, true);
        trc21Reader.FeeCapacities[XdcTxPoolTestHelper.TokenAddress] = 1;
        trc21Reader.IsValid = false;

        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>());
        AcceptTxResult result = filter.Accept(tx, ref state, TxHandlingOptions.None);

        Assert.That(result, Is.EqualTo(AcceptTxResult.InsufficientFunds));
        Assert.That(trc21Reader.ValidateCalls, Is.EqualTo(1));
    }

    [Test]
    public void Accept_ShouldSkipTrc21Validation_WhenRecipientIsNotTrc21Token()
    {
        Transaction tx = XdcTxPoolTestHelper.BuildTx(XdcTxPoolTestHelper.SenderAddress, XdcTxPoolTestHelper.RecipientAddress);
        (XdcIncomingTxFilter filter, XdcTxPoolTestHelper.FakeTrc21StateReader trc21Reader) = CreateFilter(100, true);

        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>());
        AcceptTxResult result = filter.Accept(tx, ref state, TxHandlingOptions.None);

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
        Assert.That(trc21Reader.ValidateCalls, Is.EqualTo(0));
    }

    [Test]
    public void Accept_ShouldSkipTrc21Validation_WhenFeatureIsDisabled()
    {
        Transaction tx = XdcTxPoolTestHelper.BuildTx(XdcTxPoolTestHelper.SenderAddress, XdcTxPoolTestHelper.TokenAddress);
        (XdcIncomingTxFilter filter, XdcTxPoolTestHelper.FakeTrc21StateReader trc21Reader) = CreateFilter(100, false);
        trc21Reader.FeeCapacities[XdcTxPoolTestHelper.TokenAddress] = 1;
        trc21Reader.IsValid = false;

        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>());
        AcceptTxResult result = filter.Accept(tx, ref state, TxHandlingOptions.None);

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
        Assert.That(trc21Reader.ValidateCalls, Is.EqualTo(0));
    }

    [Test]
    public void Accept_ShouldRejectNonSpecialTx_BelowMinGasPriceBefore50x()
    {
        Transaction tx = XdcTxPoolTestHelper.BuildTx(
            XdcTxPoolTestHelper.SenderAddress,
            XdcTxPoolTestHelper.RecipientAddress,
            gasPrice: XdcConstants.Trc21GasPrice - 1);
        (XdcIncomingTxFilter filter, _) = CreateFilter(100, true, blockNumberGas50x: 1_000);

        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>());
        AcceptTxResult result = filter.Accept(tx, ref state, TxHandlingOptions.None);

        Assert.That(result, Is.EqualTo(AcceptTxResult.FeeTooLow));
    }

    [Test]
    public void Accept_ShouldRejectNonSpecialTx_BelowMinGasPriceAfter50x()
    {
        Transaction tx = XdcTxPoolTestHelper.BuildTx(
            XdcTxPoolTestHelper.SenderAddress,
            XdcTxPoolTestHelper.RecipientAddress,
            gasPrice: XdcConstants.Trc21GasPrice50x - 1);
        (XdcIncomingTxFilter filter, _) = CreateFilter(100, true, blockNumberGas50x: 10);

        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>());
        AcceptTxResult result = filter.Accept(tx, ref state, TxHandlingOptions.None);

        Assert.That(result, Is.EqualTo(AcceptTxResult.FeeTooLow));
    }

    [Test]
    public void Accept_ShouldAcceptNonSpecialTx_AtBoundaryMinGasPrices()
    {
        Transaction before50XTx = XdcTxPoolTestHelper.BuildTx(
            XdcTxPoolTestHelper.SenderAddress,
            XdcTxPoolTestHelper.RecipientAddress,
            gasPrice: XdcConstants.Trc21GasPrice);
        (XdcIncomingTxFilter before50XFilter, _) = CreateFilter(100, true, blockNumberGas50x: 1_000);
        TxFilteringState beforeState = new(before50XTx, Substitute.For<IAccountStateProvider>());
        AcceptTxResult beforeResult = before50XFilter.Accept(before50XTx, ref beforeState, TxHandlingOptions.None);

        Transaction after50XTx = XdcTxPoolTestHelper.BuildTx(
            XdcTxPoolTestHelper.SenderAddress,
            XdcTxPoolTestHelper.RecipientAddress,
            gasPrice: XdcConstants.Trc21GasPrice50x);
        (XdcIncomingTxFilter after50xFilter, _) = CreateFilter(100, true, blockNumberGas50x: 10);
        TxFilteringState afterState = new(after50XTx, Substitute.For<IAccountStateProvider>());
        AcceptTxResult afterResult = after50xFilter.Accept(after50XTx, ref afterState, TxHandlingOptions.None);

        Assert.That(beforeResult, Is.EqualTo(AcceptTxResult.Accepted));
        Assert.That(afterResult, Is.EqualTo(AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_ShouldSkipMinGasPriceCheck_ForSpecialTx()
    {
        Transaction tx = XdcTxPoolTestHelper.BuildTx(
            XdcTxPoolTestHelper.SignerAddress,
            XdcTxPoolTestHelper.RandomizeContract,
            gasPrice: 1);
        (XdcIncomingTxFilter filter, _) = CreateFilter(100, true, blockNumberGas50x: 1);

        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>());
        AcceptTxResult result = filter.Accept(tx, ref state, TxHandlingOptions.None);

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
    }

    private static (XdcIncomingTxFilter, XdcTxPoolTestHelper.FakeTrc21StateReader) CreateFilter(long headNumber, bool isTipTrc21FeeEnabled, long blockNumberGas50x = long.MaxValue)
    {
        (IBlockTree blockTree, ISpecProvider specProvider) = XdcTxPoolTestHelper.Create(
            headNumber,
            isTipTrc21FeeEnabled,
            blockNumberGas50x);

        ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
        Snapshot snapshot = new(headNumber, Keccak.Zero, [XdcTxPoolTestHelper.SignerAddress]);
        snapshotManager.GetSnapshotByBlockNumber(Arg.Any<long>(), Arg.Any<IXdcReleaseSpec>()).Returns(snapshot);

        XdcTxPoolTestHelper.FakeTrc21StateReader trc21Reader = new();
        return (new XdcIncomingTxFilter(snapshotManager, blockTree, specProvider, trc21Reader), trc21Reader);
    }
}
