// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs.Forks;
using Nethermind.State.Flat.History.Changesets;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class PostTransactionWritersTests
{
    /// <summary>The addresses block processing writes outside a transaction: the two system calls around the
    /// transactions (Nethermind.Consensus <c>StoreBeaconRoot</c>, <c>ApplyBlockhashStateChanges</c>) and the four the
    /// execution-requests processor dequeues from afterwards. A fork that adds one fails here first.</summary>
    private static readonly Address[] SystemContracts =
    [
        Eip4788Constants.BeaconRootsAddress,
        Eip2935Constants.BlockHashHistoryAddress,
        Eip7002Constants.WithdrawalRequestPredeployAddress,
        Eip7251Constants.ConsolidationRequestPredeployAddress,
        Eip8282Constants.BuilderDepositRequestPredeployAddress,
        Eip8282Constants.BuilderExitRequestPredeployAddress,
    ];

    [Test]
    public void EverySystemContractOfTheLatestFork_IsRefusedByTheChain()
    {
        HashSet<AddressAsKey> writers = [];

        PostTransactionWriters.AddSystemContracts(Prague.Instance, writers);

        using (Assert.EnterMultipleScope())
        {
            foreach (Address contract in SystemContracts)
            {
                Assert.That(writers, Does.Contain(new AddressAsKey(contract)), $"{contract} is written after the transactions, so a chain that answered for it would serve mid-block state");
            }
        }
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
}
