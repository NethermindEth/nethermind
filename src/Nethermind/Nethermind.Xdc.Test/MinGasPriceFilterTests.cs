// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
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
    private const ulong MinGasPrice = XdcConstants.MinGasPrice;
    private static readonly Address BlockSigner = TestItem.AddressA;
    private static readonly Address Randomize = TestItem.AddressB;

    private static MinGasPriceFilter CreateFilter(ulong headNumber = 100, ISpecProvider? specProvider = null)
    {
        IChainHeadInfoProvider chainHeadInfoProvider = Substitute.For<IChainHeadInfoProvider>();
        chainHeadInfoProvider.HeadNumber.Returns(headNumber);

        IXdcReleaseSpec xdcSpec = Substitute.For<IXdcReleaseSpec>();
        xdcSpec.BlockSignerContract.Returns(BlockSigner);
        xdcSpec.RandomizeSMCBinary.Returns(Randomize);

        specProvider ??= Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        return new MinGasPriceFilter(chainHeadInfoProvider, specProvider, LimboLogs.Instance);
    }

    private static AcceptTxResult Accept(MinGasPriceFilter filter, Transaction tx, TxHandlingOptions options = TxHandlingOptions.None)
    {
        TxFilteringState state = default;
        return filter.Accept(tx, ref state, options);
    }

    [TestCase(MinGasPrice, true, TestName = "Exactly at the minimum accepted")]
    [TestCase(MinGasPrice + 1, true, TestName = "Above the minimum accepted")]
    [TestCase(MinGasPrice - 1, false, TestName = "Below the minimum rejected")]
    [TestCase(0ul, false, TestName = "Zero gas price rejected")]
    public void Accept_ComparesLegacyGasPriceWithMinimum(ulong gasPrice, bool expectedAccepted)
    {
        MinGasPriceFilter filter = CreateFilter();
        Transaction tx = Build.A.Transaction.WithType(TxType.Legacy).WithGasPrice(gasPrice).WithTo(TestItem.AddressC).TestObject;

        Assert.That((bool)Accept(filter, tx), Is.EqualTo(expectedAccepted));
    }

    // The reference client compares tx.GasPrice(), which is the fee cap for a dynamic fee transaction. XDC's base fee
    // equals the floor, so comparing the priority fee instead would reject everything the reference accepts.
    [TestCase(MinGasPrice, 1ul, true, TestName = "Fee cap at the minimum accepted regardless of priority fee")]
    [TestCase(MinGasPrice - 1, MinGasPrice - 1, false, TestName = "Fee cap below the minimum rejected")]
    public void Accept_Uses1559FeeCap(ulong maxFeePerGas, ulong maxPriorityFeePerGas, bool expectedAccepted)
    {
        MinGasPriceFilter filter = CreateFilter();
        Transaction tx = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithMaxFeePerGas(maxFeePerGas)
            .WithMaxPriorityFeePerGas(maxPriorityFeePerGas)
            .WithTo(TestItem.AddressC)
            .TestObject;

        Assert.That((bool)Accept(filter, tx), Is.EqualTo(expectedAccepted));
    }

    [Test]
    public void Accept_SpecialTransactions_AreAcceptedWithoutPayingAnything()
    {
        MinGasPriceFilter filter = CreateFilter();

        Assert.Multiple(() =>
        {
            Assert.That(Accept(filter, Build.A.Transaction.WithGasPrice(0).WithTo(BlockSigner).TestObject), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(Accept(filter, Build.A.Transaction.WithGasPrice(0).WithTo(Randomize).TestObject), Is.EqualTo(AcceptTxResult.Accepted));
        });
    }

    [Test]
    public void Accept_UnderpricedLocalTransaction_IsRejected()
    {
        MinGasPriceFilter filter = CreateFilter();
        Transaction tx = Build.A.Transaction.WithGasPrice(1).WithTo(TestItem.AddressC).TestObject;

        Assert.That(Accept(filter, tx, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.FeeTooLow));
    }

    [Test]
    public void Accept_ContractCreation_IsSubjectToTheMinimum()
    {
        MinGasPriceFilter filter = CreateFilter();

        Assert.That(Accept(filter, Build.A.Transaction.WithGasPrice(1).WithTo(null).TestObject), Is.EqualTo(AcceptTxResult.FeeTooLow));
    }

    [TestCase(1ul, false, TestName = "Pool rejects underpriced transaction")]
    [TestCase(MinGasPrice, true, TestName = "Pool accepts transaction at the minimum")]
    public async Task SubmitTx_UnderMinGasPrice_IsRejectedOnPoolAdmission(ulong gasPrice, bool expectedAccepted)
    {
        using XdcTestBlockchain chain = await XdcTestBlockchain.Create(5, false);

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
