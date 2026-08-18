// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
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
internal class BlackListedAddressFilterTests
{
    private static readonly Address BlackListed = TestItem.AddressA;

    private static BlackListedAddressFilter CreateFilter(ulong headNumber, bool blackListingEnabled, ISpecProvider? specProvider = null)
    {
        IChainHeadInfoProvider chainHeadInfoProvider = Substitute.For<IChainHeadInfoProvider>();
        chainHeadInfoProvider.HeadNumber.Returns(headNumber);

        IXdcReleaseSpec xdcSpec = Substitute.For<IXdcReleaseSpec>();
        xdcSpec.IsBlackListingEnabled.Returns(blackListingEnabled);
        HashSet<Address> blackList = [BlackListed];
        xdcSpec.BlackListedAddresses.Returns(blackList);

        specProvider ??= Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        return new BlackListedAddressFilter(chainHeadInfoProvider, specProvider, LimboLogs.Instance);
    }

    private static AcceptTxResult Accept(BlackListedAddressFilter filter, Transaction tx)
    {
        TxFilteringState state = default;
        return filter.Accept(tx, ref state, TxHandlingOptions.None);
    }

    private static Transaction BuildTx(Address? sender, Address? to) =>
        Build.A.Transaction.WithSenderAddress(sender).WithTo(to).TestObject;

    [TestCase(true, false, true, false, TestName = "Blacklisted sender rejected once activated")]
    [TestCase(true, false, false, true, TestName = "Blacklisted sender allowed before activation")]
    [TestCase(false, true, true, false, TestName = "Blacklisted recipient rejected once activated")]
    [TestCase(false, true, false, true, TestName = "Blacklisted recipient allowed before activation")]
    [TestCase(false, false, true, true, TestName = "Unlisted addresses accepted once activated")]
    [TestCase(false, false, false, true, TestName = "Unlisted addresses accepted before activation")]
    public void Accept_ChecksSenderAndRecipient(bool blackListSender, bool blackListRecipient, bool blackListingEnabled, bool expectedAccepted)
    {
        BlackListedAddressFilter filter = CreateFilter(headNumber: 100, blackListingEnabled);
        Transaction tx = BuildTx(blackListSender ? BlackListed : TestItem.AddressB, blackListRecipient ? BlackListed : TestItem.AddressC);

        Assert.That((bool)Accept(filter, tx), Is.EqualTo(expectedAccepted));
    }

    [TestCase(true, "sender")]
    [TestCase(false, "recipient")]
    public void Accept_BlackListedAddress_ReportsRoleWithoutDisconnectingPeer(bool blackListSender, string expectedRole)
    {
        BlackListedAddressFilter filter = CreateFilter(headNumber: 100, blackListingEnabled: true);
        Transaction tx = blackListSender ? BuildTx(BlackListed, TestItem.AddressC) : BuildTx(TestItem.AddressB, BlackListed);

        AcceptTxResult result = Accept(filter, tx);

        Assert.That(result, Is.EqualTo(XdcAcceptTxResult.BlackListedSender));
        Assert.That(result.ToString(), Does.Contain(expectedRole));
        Assert.That(result, Is.Not.EqualTo(AcceptTxResult.Invalid), "Invalid makes TxFloodController disconnect the relaying peer");
    }

    [Test]
    public void Accept_ContractCreation_IsAccepted()
    {
        BlackListedAddressFilter filter = CreateFilter(headNumber: 100, blackListingEnabled: true);

        Assert.That(Accept(filter, BuildTx(TestItem.AddressB, to: null)), Is.EqualTo(AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_UsesSpecOfBlockAfterHead()
    {
        const ulong headNumber = 1234;
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        BlackListedAddressFilter filter = CreateFilter(headNumber, blackListingEnabled: true, specProvider);

        Accept(filter, BuildTx(TestItem.AddressB, TestItem.AddressC));

        specProvider.Received().GetSpec(Arg.Is<ForkActivation>(f => f.BlockNumber == headNumber + 1));
    }

    [TestCase(true, true, false, TestName = "Pool rejects blacklisted sender")]
    [TestCase(false, true, false, TestName = "Pool rejects blacklisted recipient")]
    [TestCase(true, false, true, TestName = "Pool accepts blacklisted sender before activation")]
    public async Task SubmitTx_BlackListedAddress_IsRejectedOnPoolAdmission(bool blackListSender, bool blackListingEnabled, bool expectedAccepted)
    {
        using XdcTestBlockchain chain = await XdcTestBlockchain.Create(5, false);
        chain.ChangeReleaseSpec(spec =>
        {
            spec.BlackListedAddresses = [blackListSender ? TestItem.AddressB : TestItem.AddressC];
            spec.IsBlackListingEnabled = blackListingEnabled;
        });

        Transaction tx = Build.A.Transaction
            .WithSenderAddress(TestItem.AddressB)
            .WithTo(TestItem.AddressC)
            .WithValue(1)
            .WithType(TxType.Legacy)
            .WithNonce(chain.TxPool.GetLatestPendingNonce(TestItem.AddressB))
            .TestObject;
        new Signer(chain.SpecProvider.ChainId, TestItem.PrivateKeyB, NullLogManager.Instance).TrySign(tx);
        tx.Hash = tx.CalculateHash();

        AcceptTxResult result = chain.TxPool.SubmitTx(tx, TxHandlingOptions.None);

        Assert.That((bool)result, Is.EqualTo(expectedAccepted), result.ToString());
        if (!expectedAccepted)
            Assert.That(result, Is.EqualTo(blackListSender ? XdcAcceptTxResult.BlackListedSender : XdcAcceptTxResult.BlackListedRecipient));
        Assert.That(chain.TxPool.GetPendingTransactions(), Has.Length.EqualTo(expectedAccepted ? 1 : 0));
    }
}
