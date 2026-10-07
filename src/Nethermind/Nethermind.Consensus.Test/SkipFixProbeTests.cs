// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

/// <summary>Verifier probes for bench/handoff-matches-skip (5760a35262); not for merge.</summary>
[TestFixtureSource(nameof(Forks))]
public class SkipFixProbeTests(IReleaseSpec spec) : PrewarmerHandoffTestBase(spec)
{
    private static readonly IReleaseSpec[] Forks = [Osaka.Instance, Shanghai.Instance, WithoutEip3607()];

    private static IReleaseSpec WithoutEip3607()
    {
        ReleaseSpec spec = ((ReleaseSpec)Osaka.Instance).Clone();
        spec.IsEip3607Enabled = false;
        spec.Name = "Osaka-no3607";
        return spec;
    }

    // P1: a slot the block writes before its transactions (as the EIP-4788 / EIP-2935 system calls do) is compared.
    [Test]
    public void P1_A_slot_written_before_the_transactions_is_compared([Values(0x4e4dul, 0ul)] ulong value)
    {
        void Before(IWorldState state)
        {
            state.Set(new StorageCell(Counter, 0), value);
            state.Commit(Spec, commitRoots: false);
        }

        Run run = Handoff(BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Counter),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 1.Wei),
            Call(TestItem.PrivateKeyD, 0, Logger)), beforeTransactions: Before);

        Assert.That(run.Tally.Rejected, Is.EqualTo(value == 0 ? 0 : 1));
    }

    // P2: a slot a replay clears without reading it first (the skip left it unread) is compared by a later reader.
    [Test]
    public void P2_A_slot_a_replay_zeroes_blind_is_compared_by_a_later_first_run()
    {
        Run run = Handoff(BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Zeroer),
            Call(TestItem.PrivateKeyB, 0, Zeroer, data: [1]),
            Transfer(TestItem.PrivateKeyD, 0, TestItem.AddressC, 1.Wei)));

        Assert.That((run.Tally.Replayed, run.Tally.Rejected), Is.EqualTo((2, 1)));
    }

    // P3: a balance the block changed is compared when a sender's first transaction reads it (account skip live without EIP-3607).
    [Test]
    public void P3_A_balance_the_block_changed_is_compared_by_a_first_run()
    {
        Run run = Handoff(BuildBlock(
            Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressC, 1.Wei),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 2.Wei),
            Call(TestItem.PrivateKeyD, 0, BalanceReader)));

        Assert.That((run.Tally.Replayed, run.Tally.Rejected), Is.EqualTo((2, 1)));
    }

    // P4: an account the block changes before its transactions (as a system call or the DAO transition would) is compared.
    [Test]
    public void P4_An_account_changed_before_the_transactions_is_compared()
    {
        void Before(IWorldState state)
        {
            state.AddToBalance(TestItem.AddressC, 5, Spec, out _);
            state.Commit(Spec, commitRoots: false);
        }

        Run run = Handoff(BuildBlock(
            Call(TestItem.PrivateKeyD, 0, BalanceReader),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressA, 1.Wei),
            Call(TestItem.PrivateKeyA, 0, Logger)), beforeTransactions: Before);

        Assert.That(run.Tally.Rejected, Is.EqualTo(1));
    }

    // P5: a contract the block clears before a first run reads its slot (pre-Cancun self-destruct and redeploy) is compared.
    [Test]
    public void P5_A_slot_of_a_contract_cleared_earlier_in_the_block_is_compared()
    {
        Run run = Handoff(BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Child),
            Call(TestItem.PrivateKeyB, 0, Factory, gasLimit: 300_000),
            Call(TestItem.PrivateKeyD, 0, Child, data: [1])));

        Assert.That(run.Tally.Rejected, Is.GreaterThanOrEqualTo(Spec.IsEip6780Enabled ? 0 : 1));
    }
}
