// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.Validators;

/// <summary>Port of the round-robin cases of Besu's <c>ProposerSelectorTest</c>; QBFT always rotates the proposer per block.</summary>
[Parallelizable(ParallelScope.All)]
public class BftProposerSelectorTests
{
    private const long PrevBlockNumber = 2;
    private static readonly Address LocalAddress = QbftTestData.Addr(10);

    /// <summary>Sorted list with <paramref name="countLower"/> addresses below and <paramref name="countHigher"/> above the local one.</summary>
    private static List<Address> CreateValidatorList(Address local, int countLower, int countHigher)
    {
        List<Address> result = [local];
        for (int i = 0; i < countLower; i++) result.Add(QbftTestData.Addr((ulong)(10 - countLower + i)));
        for (int i = 0; i < countHigher; i++) result.Add(QbftTestData.Addr((ulong)(11 + i)));
        result.Sort();
        return result;
    }

    private static BftProposerSelector CreateSelector(Address previousProposer, IReadOnlyList<Address> validators)
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber((ulong)PrevBlockNumber).WithBeneficiary(previousProposer).TestObject;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader((ulong)PrevBlockNumber, BlockTreeLookupOptions.RequireCanonical).Returns(parent);
        IValidatorProvider validatorProvider = Substitute.For<IValidatorProvider>();
        validatorProvider.GetValidatorsAfterBlock(parent).Returns(validators);
        return new BftProposerSelector(blockTree, validatorProvider);
    }

    [Test]
    public void RoundRobinChangesProposerOnRoundZeroOfNextBlock()
    {
        List<Address> validators = CreateValidatorList(LocalAddress, 0, 4);
        Address next = CreateSelector(LocalAddress, validators).SelectProposerForRound(new ConsensusRoundIdentifier(PrevBlockNumber + 1, 0));
        Assert.That(next, Is.EqualTo(validators[1]));
    }

    [Test]
    public void LastValidatorInListValidatedPreviousBlockSoFirstIsNextProposer()
    {
        List<Address> validators = CreateValidatorList(LocalAddress, 4, 0);
        Address next = CreateSelector(LocalAddress, validators).SelectProposerForRound(new ConsensusRoundIdentifier(PrevBlockNumber + 1, 0));
        Assert.That(next, Is.EqualTo(validators[0]));
    }

    [Test]
    public void EachRoundAdvancesOneValidator()
    {
        List<Address> validators = CreateValidatorList(LocalAddress, 4, 0);
        BftProposerSelector selector = CreateSelector(LocalAddress, validators);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(selector.SelectProposerForRound(new ConsensusRoundIdentifier(PrevBlockNumber + 1, 0)), Is.EqualTo(validators[0]));
            Assert.That(selector.SelectProposerForRound(new ConsensusRoundIdentifier(PrevBlockNumber + 1, 1)), Is.EqualTo(validators[1]));
            Assert.That(selector.SelectProposerForRound(new ConsensusRoundIdentifier(PrevBlockNumber + 1, 2)), Is.EqualTo(validators[2]));
        }
    }

    [Test]
    public void WhenProposerSelfRemovesSelectsNextProposerInLine()
    {
        List<Address> validators = CreateValidatorList(LocalAddress, 2, 2);
        validators.Remove(LocalAddress);
        Address next = CreateSelector(LocalAddress, validators).SelectProposerForRound(new ConsensusRoundIdentifier(PrevBlockNumber + 1, 0));
        Assert.That(next, Is.EqualTo(validators[2]));
    }

    [Test]
    public void ProposerSelfRemovesAndHasHighestAddressNewProposerIsFirstInList()
    {
        List<Address> validators = CreateValidatorList(LocalAddress, 4, 0);
        validators.Remove(LocalAddress);
        Address next = CreateSelector(LocalAddress, validators).SelectProposerForRound(new ConsensusRoundIdentifier(PrevBlockNumber + 1, 0));
        Assert.That(next, Is.EqualTo(validators[0]));
    }

    [Test]
    public void UnknownParentThrows()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        BftProposerSelector selector = new(blockTree, Substitute.For<IValidatorProvider>());
        Assert.That(() => selector.SelectProposerForRound(new ConsensusRoundIdentifier(PrevBlockNumber + 1, 0)), Throws.InvalidOperationException);
    }

    [Test]
    public void SelectionIsIndependentOfInputOrder()
    {
        Address[] unsorted = [QbftTestData.Addr(30), QbftTestData.Addr(10), QbftTestData.Addr(20)];
        Address next = BftProposerSelector.SelectProposerForRound(new ConsensusRoundIdentifier(1, 0), QbftTestData.Addr(10), unsorted);
        Assert.That(next, Is.EqualTo(QbftTestData.Addr(20)));
    }

    [Test]
    public void InvalidRoundIdentifierThrows()
    {
        BftProposerSelector selector = new(Substitute.For<IBlockTree>(), Substitute.For<IValidatorProvider>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => selector.SelectProposerForRound(new ConsensusRoundIdentifier(0, 0)), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => selector.SelectProposerForRound(new ConsensusRoundIdentifier(1, -1)), Throws.InstanceOf<ArgumentOutOfRangeException>());
        }
    }
}
