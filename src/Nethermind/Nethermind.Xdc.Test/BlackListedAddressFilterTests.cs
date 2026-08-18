// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.TxPool;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[Parallelizable(ParallelScope.All)]
internal class BlackListedAddressFilterTests
{
    private static readonly Address BlackListed = TestItem.AddressA;

    private static BlackListedAddressFilter CreateFilter(Block? head, bool blackListingEnabled, ISpecProvider? specProvider = null)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(head);

        IXdcReleaseSpec xdcSpec = Substitute.For<IXdcReleaseSpec>();
        xdcSpec.IsBlackListingEnabled.Returns(blackListingEnabled);
        xdcSpec.BlackListedAddresses.Returns(new HashSet<Address> { BlackListed });

        specProvider ??= Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        return new BlackListedAddressFilter(blockTree, specProvider);
    }

    private static Block HeadBlock(ulong number = 100) =>
        Build.A.Block.WithHeader(Build.A.XdcBlockHeader().WithNumber(number).TestObject).TestObject;

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
        BlackListedAddressFilter filter = CreateFilter(HeadBlock(), blackListingEnabled);
        Transaction tx = BuildTx(blackListSender ? BlackListed : TestItem.AddressB, blackListRecipient ? BlackListed : TestItem.AddressC);

        Assert.That((bool)Accept(filter, tx), Is.EqualTo(expectedAccepted));
    }

    [Test]
    public void Accept_NullHead_ReturnsSyncing()
    {
        BlackListedAddressFilter filter = CreateFilter(head: null, blackListingEnabled: true);

        Assert.That(Accept(filter, BuildTx(TestItem.AddressB, TestItem.AddressC)), Is.EqualTo(AcceptTxResult.Syncing));
    }

    [Test]
    public void Accept_ContractCreation_IsAccepted()
    {
        BlackListedAddressFilter filter = CreateFilter(HeadBlock(), blackListingEnabled: true);

        Assert.That(Accept(filter, BuildTx(TestItem.AddressB, to: null)), Is.EqualTo(AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_UsesSpecOfCurrentHead()
    {
        const ulong headNumber = 1234;
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        BlackListedAddressFilter filter = CreateFilter(HeadBlock(headNumber), blackListingEnabled: true, specProvider);

        Accept(filter, BuildTx(TestItem.AddressB, TestItem.AddressC));

        specProvider.Received().GetSpec(Arg.Is<ForkActivation>(f => f.BlockNumber == headNumber));
    }
}
