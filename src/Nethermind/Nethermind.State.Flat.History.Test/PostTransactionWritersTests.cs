// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State.Flat.History.Changesets;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class PostTransactionWritersTests
{
    /// <summary>Every contract address the spec names, whichever fork is asked. The block's system calls reach them
    /// around the transactions, so a chain that answered for one would serve mid-block state; a fork that adds one
    /// fails here until <see cref="PostTransactionWriters"/> handles it.</summary>
    private static IEnumerable<PropertyInfo> SpecContractAddresses => typeof(IReleaseSpec).GetProperties()
        .Where(property => property.PropertyType == typeof(Address) && property.Name.EndsWith("ContractAddress"));

    [Test]
    public void EveryContractAddressTheSpecNames_IsRefusedByTheChain()
    {
        IReleaseSpec spec = Bogota.Instance;
        HashSet<AddressAsKey> writers = [];

        PostTransactionWriters.AddSystemContracts(spec, writers);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SpecContractAddresses.Any(), Is.True, "precondition: the spec still names its contracts this way, or this test checks nothing");
            foreach (PropertyInfo property in SpecContractAddresses)
            {
                if (property.GetValue(spec) is not Address address) continue;

                Assert.That(writers, Does.Contain(new AddressAsKey(address)), $"{property.Name} is written outside a transaction, so a chain that answered for it would serve mid-block state");
            }
        }
    }

    [Test]
    public void ThePredeploysThatAreConstantsRatherThanSpecProperties_AreRefusedWhicheverForkIsAsked()
    {
        HashSet<AddressAsKey> writers = [];

        PostTransactionWriters.AddSystemContracts(Prague.Instance, writers);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(writers, Does.Contain(new AddressAsKey(Eip8282Constants.BuilderDepositRequestPredeployAddress)), "the execution-requests processor dequeues from it after the transactions");
            Assert.That(writers, Does.Contain(new AddressAsKey(Eip8282Constants.BuilderExitRequestPredeployAddress)));
        }
    }

    [Test]
    public void TheAccountAChainSpecExemptsFromPruning_IsRefused()
    {
        HashSet<AddressAsKey> writers = [];

        PostTransactionWriters.AddSystemContracts(new ReleaseSpec { Eip158IgnoredAccount = Address.SystemUser }, writers);

        Assert.That(writers, Does.Contain(new AddressAsKey(Address.SystemUser)), "a system call can leave the exempt account written after the transactions");
    }

    [Test]
    public void TheBeneficiaryAndTheWithdrawalRecipients_AreRefused()
    {
        Block block = Build.A.Block.WithNumber(7).WithBeneficiary(TestItem.AddressB)
            .WithWithdrawals([new Withdrawal { Address = TestItem.AddressA, AmountInGwei = 1 }]).TestObject;
        HashSet<AddressAsKey> writers = [];

        bool collected = PostTransactionWriters.TryCollect(block, Prague.Instance, writers);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(collected, Is.True);
            Assert.That(writers, Does.Contain(new AddressAsKey(TestItem.AddressB)), "the reward reaches the beneficiary after the transactions");
            Assert.That(writers, Does.Contain(new AddressAsKey(TestItem.AddressA)), "the withdrawal credits the recipient after the transactions");
        }
    }

    [TestCase(SealEngineType.Ethash, true)]
    [TestCase(SealEngineType.BeaconChain, true)]
    [TestCase(SealEngineType.Clique, true)]
    [TestCase(SealEngineType.AuRa, false)]
    [TestCase(SealEngineType.Optimism, false)]
    [TestCase(SealEngineType.Taiko, false)]
    public void OnlyTheSealEnginesWhoseBlockProcessingIsDescribedHere_AreChained(string sealEngine, bool described) =>
        Assert.That(PostTransactionWriters.Describes(sealEngine), Is.EqualTo(described),
            "an AuRa chain credits withdrawals through a chainspec contract whose writes no spec property names, and Optimism and Taiko carry their own block processors");
}
