// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[Parallelizable(ParallelScope.All)]
internal class MinGasPriceFilterTests
{
    private const ulong MinGasPrice = XdcConstants.DefaultMinGasPrice;
    private static readonly Address BlockSigner = new("0x00000000000000000000000000000000b000089");

    private static MinGasPriceFilter CreateFilter(UInt256 minimumGasPrice, ulong headNumber = 100, ISpecProvider? specProvider = null)
    {
        IChainHeadInfoProvider chainHeadInfoProvider = Substitute.For<IChainHeadInfoProvider>();
        chainHeadInfoProvider.HeadNumber.Returns(headNumber);

        IXdcReleaseSpec xdcSpec = Substitute.For<IXdcReleaseSpec>();
        xdcSpec.MinimumGasPrice.Returns(minimumGasPrice);
        xdcSpec.BlockSignerContract.Returns(BlockSigner);

        specProvider ??= Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        return new MinGasPriceFilter(chainHeadInfoProvider, specProvider, LimboLogs.Instance);
    }

    private static AcceptTxResult Accept(MinGasPriceFilter filter, Transaction tx)
    {
        TxFilteringState state = default;
        return filter.Accept(tx, ref state, TxHandlingOptions.None);
    }

    [TestCase(MinGasPrice, true, TestName = "Exactly at the minimum accepted")]
    [TestCase(MinGasPrice + 1, true, TestName = "Above the minimum accepted")]
    [TestCase(MinGasPrice - 1, false, TestName = "Below the minimum rejected")]
    [TestCase(0ul, false, TestName = "Zero gas price rejected")]
    public void Accept_ComparesLegacyGasPriceWithMinimum(ulong gasPrice, bool expectedAccepted)
    {
        MinGasPriceFilter filter = CreateFilter(MinGasPrice);
        Transaction tx = Build.A.Transaction.WithType(TxType.Legacy).WithGasPrice(gasPrice).WithTo(TestItem.AddressC).TestObject;

        Assert.That((bool)Accept(filter, tx), Is.EqualTo(expectedAccepted));
    }

    // The reference client compares tx.GasPrice(), which is the fee cap for a dynamic fee transaction, so a low
    // priority fee alone is not a reason to reject.
    [TestCase(MinGasPrice, 1ul, true, TestName = "Fee cap at the minimum accepted regardless of priority fee")]
    [TestCase(MinGasPrice - 1, MinGasPrice - 1, false, TestName = "Fee cap below the minimum rejected")]
    public void Accept_Uses1559FeeCap(ulong maxFeePerGas, ulong maxPriorityFeePerGas, bool expectedAccepted)
    {
        MinGasPriceFilter filter = CreateFilter(MinGasPrice);
        Transaction tx = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithMaxFeePerGas(maxFeePerGas)
            .WithMaxPriorityFeePerGas(maxPriorityFeePerGas)
            .WithTo(TestItem.AddressC)
            .TestObject;

        Assert.That((bool)Accept(filter, tx), Is.EqualTo(expectedAccepted));
    }

    [Test]
    public void Accept_SpecialTransaction_IsAcceptedWithoutPayingAnything()
    {
        MinGasPriceFilter filter = CreateFilter(MinGasPrice);
        Transaction tx = Build.A.Transaction.WithGasPrice(0).WithTo(BlockSigner).TestObject;

        Assert.That(Accept(filter, tx), Is.EqualTo(AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_MinimumNotConfigured_IsInert()
    {
        MinGasPriceFilter filter = CreateFilter(UInt256.Zero);
        Transaction tx = Build.A.Transaction.WithGasPrice(0).WithTo(TestItem.AddressC).TestObject;

        Assert.That(Accept(filter, tx), Is.EqualTo(AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_UnderpricedLocalTransaction_IsRejected()
    {
        MinGasPriceFilter filter = CreateFilter(MinGasPrice);
        Transaction tx = Build.A.Transaction.WithGasPrice(1).WithTo(TestItem.AddressC).TestObject;
        TxFilteringState state = default;

        AcceptTxResult result = filter.Accept(tx, ref state, TxHandlingOptions.PersistentBroadcast);

        Assert.That(result, Is.EqualTo(AcceptTxResult.FeeTooLow));
    }

    [Test]
    public void Accept_UsesSpecOfHead()
    {
        const ulong headNumber = 1234;
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        MinGasPriceFilter filter = CreateFilter(MinGasPrice, headNumber, specProvider);

        Accept(filter, Build.A.Transaction.WithGasPrice(MinGasPrice).WithTo(TestItem.AddressC).TestObject);

        specProvider.Received().GetSpec(Arg.Is<ForkActivation>(f => f.BlockNumber == headNumber));
    }

    [TestCase(1ul, false, TestName = "Pool rejects underpriced transaction")]
    [TestCase(MinGasPrice, true, TestName = "Pool accepts transaction at the minimum")]
    public async Task SubmitTx_UnderMinGasPrice_IsRejectedOnPoolAdmission(ulong gasPrice, bool expectedAccepted)
    {
        using XdcTestBlockchain chain = await XdcTestBlockchain.Create(5, false);
        chain.ChangeReleaseSpec(spec => spec.MinimumGasPrice = MinGasPrice);

        Transaction tx = Build.A.Transaction
            .WithSenderAddress(TestItem.AddressB)
            .WithTo(TestItem.AddressC)
            .WithValue(1)
            .WithGasPrice(gasPrice)
            .WithType(TxType.Legacy)
            .WithNonce(chain.TxPool.GetLatestPendingNonce(TestItem.AddressB))
            .TestObject;
        new Signer(chain.SpecProvider.ChainId, TestItem.PrivateKeyB, NullLogManager.Instance).TrySign(tx);
        tx.Hash = tx.CalculateHash();

        AcceptTxResult result = chain.TxPool.SubmitTx(tx, TxHandlingOptions.None);

        Assert.That((bool)result, Is.EqualTo(expectedAccepted), result.ToString());
        if (!expectedAccepted)
            Assert.That(result, Is.EqualTo(AcceptTxResult.FeeTooLow));
        Assert.That(chain.TxPool.GetPendingTransactions(), Has.Length.EqualTo(expectedAccepted ? 1 : 0));
    }
}
