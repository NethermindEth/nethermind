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
    /// <summary>Every address the spec can name, on the interface and on the ones it derives from. A fork enables
    /// its own contracts, so each fork is asked separately: a property a later fork introduces reads null on the
    /// forks before it, and only that fork's own instance shows it.</summary>
    private static IEnumerable<PropertyInfo> SpecAddresses => typeof(IReleaseSpec).GetInterfaces().Append(typeof(IReleaseSpec))
        .SelectMany(static contract => contract.GetProperties())
        .Where(static property => property.PropertyType == typeof(Address) && property.Name.EndsWith("Address"))
        .DistinctBy(static property => property.Name);

    /// <summary>Every fork in the tree, so a fork added tomorrow is covered without touching this test.</summary>
    private static IEnumerable<IReleaseSpec> Forks => typeof(Bogota).Assembly.GetTypes()
        .Where(static type => type is { IsAbstract: false, IsGenericTypeDefinition: false } && typeof(IReleaseSpec).IsAssignableFrom(type))
        .Select(static type => type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null) as IReleaseSpec)
        .Where(static spec => spec is not null)!;

    [Test]
    public void EveryContractAddressEveryForkNames_IsRefusedByTheChain()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SpecAddresses.Any(), Is.True, "precondition: the spec still names its contracts this way, or this test checks nothing");
            Assert.That(Forks.Count(), Is.GreaterThan(1), "precondition: the forks are still found by their Instance property, or this test checks one thing twice");

            foreach (IReleaseSpec spec in Forks)
            {
                HashSet<AddressAsKey> writers = [];
                PostTransactionWriters.AddSystemContracts(spec, writers);

                foreach (PropertyInfo property in SpecAddresses)
                {
                    if (property.GetValue(spec) is not Address address) continue;

                    Assert.That(writers, Does.Contain(new AddressAsKey(address)),
                        $"{spec.Name} names {property.Name}, which block processing writes outside a transaction, so a chain that answered for it would serve mid-block state");
                }
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
