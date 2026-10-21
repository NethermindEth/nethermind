// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

/// <summary>Every handoff scenario with every takeover committing on its own, the path the switch turns back to.</summary>
[TestFixtureSource(typeof(PrewarmerHandoffTests), nameof(PrewarmerHandoffTests.Forks))]
public class PrewarmerHandoffPerTransactionCommitTests(IReleaseSpec spec) : PrewarmerHandoffScenarios(spec)
{
    protected override void ConfigureBlocks(BlocksConfig config) => config.PreWarmBatchedReplayCommit = false;
}

/// <summary>Every read-ahead safety scenario, the randomized blocks included, with every takeover committing on its own.</summary>
[TestFixtureSource(typeof(PrewarmerReadAheadSafetyTests), nameof(PrewarmerReadAheadSafetyTests.Forks))]
public class PrewarmerReadAheadSafetyPerTransactionCommitTests(IReleaseSpec spec) : PrewarmerReadAheadSafetyScenarios(spec)
{
    protected override void ConfigureBlocks(BlocksConfig config)
    {
        base.ConfigureBlocks(config);
        config.PreWarmBatchedReplayCommit = false;
    }
}

/// <summary>
/// Consecutive takeovers that change nothing but balances, nonces and slots share a commit, and the block ends in the state
/// and receipts of executing it: every other takeover and every executed transaction starts from committed state, and so
/// does a takeover after one that touched an empty account (EIP-161).
/// </summary>
[TestFixtureSource(nameof(Forks))]
public class PrewarmerSharedCommitTests(IReleaseSpec spec) : PrewarmerHandoffTestBase(spec)
{
    private const int DefaultMaxTxs = 16;
    private static readonly IReleaseSpec[] Forks = [Osaka.Instance, Shanghai.Instance];
    // An empty account with storage, which only genesis could leave (EIP-161).
    private static readonly Address EmptyWithStorage = new("0x00000000000000000000000000000000004e4d20");
    private bool _sharing = true;
    private int _maxTxs = DefaultMaxTxs;

    private int Shared => ProductionAdapter.CommitsShared;

    protected override void ConfigureBlocks(BlocksConfig config)
    {
        config.PreWarmBatchedReplayCommit = _sharing;
        config.PreWarmBatchedReplayCommitMaxTxs = _maxTxs;
    }

    protected override void DeployGenesis(IWorldState worldState)
    {
        worldState.CreateAccount(EmptyWithStorage, 0);
        worldState.Set(new StorageCell(EmptyWithStorage, 1), 0x4e4d);
        // As genesis, which keeps empty accounts.
        worldState.Commit(Spec, NullStateTracer.Instance, isGenesis: true);
    }

    [Test]
    public void Takeovers_of_balances_nonces_and_slots_share_commits_into_the_state_and_receipts_of_executing_the_block()
    {
        List<Transaction> transactions =
        [
            Call(TestItem.PrivateKeyA, 0, Counter),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 1.Wei),
            Call(TestItem.PrivateKeyD, 0, Logger),
            Call(TestItem.PrivateKeyA, 1, Counter),
            // Reads the balance the transfer changed, so it is executed, on the state the shared commit leaves.
            Call(TestItem.PrivateKeyD, 1, BalanceReader),
            Call(TestItem.PrivateKeyB, 1, Reverter),
            Transfer(TestItem.PrivateKeyA, 2, TestItem.AddressD, 1.Wei),
        ];
        if (Spec.IsEip1153Enabled) transactions.Add(Call(TestItem.PrivateKeyC, 0, Transient));
        transactions.Add(Call(TestItem.PrivateKeyC, Spec.IsEip1153Enabled ? 1UL : 0UL, Logger));

        Run run = Handoff(BuildBlock([.. transactions]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That((run.Tally.Replayed, run.Tally.Rejected), Is.EqualTo((transactions.Count - 1, 1)));
            // The four before the executed one, and the ones after it but the block's last.
            Assert.That(Shared, Is.EqualTo(transactions.Count - 2));
        }
    }

    [Test]
    public void A_transaction_executed_after_shared_takeovers_reports_only_its_own_writes()
    {
        // The counter's write waits in the shared commit when the guarded call, which reads the balance the transfer
        // changes, is executed; collected with the call's, it would be reported as the call's and wake the refresh worker.
        Block block = BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Counter),
            Transfer(TestItem.PrivateKeyD, 0, TestItem.AddressC, 1.Wei),
            Call(TestItem.PrivateKeyB, 0, Guarded),
            Transfer(TestItem.PrivateKeyD, 1, TestItem.AddressA, 1.Wei));
        Run executed = Process(block, adapter: null);
        // The pass is waited on and over before processing, so no refresh worker takes the wakes counted here.
        RunPreWarmCaches(PreWarmer, block);
        BlockFootprints footprints = PreWarmer.Footprints!;
        int wakes = footprints.PendingWakes;

        Run run = Process(block, ProductionAdapter);

        int pending = footprints.PendingWakes;
        footprints.ApplyExecuted();
        AssertSameRun(run, executed);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((run.Tally.Replayed, run.Tally.Rejected, Shared), Is.EqualTo((3, 1, 2)));
            Assert.That(pending, Is.EqualTo(wakes));
            Assert.That(footprints.ValueBefore(new StorageCell(Guarded, 1), 3), Is.EqualTo((UInt256)1));
            Assert.That(footprints.ValueBefore(new StorageCell(Counter, 0), 3), Is.EqualTo((UInt256)1));
        }
    }

    [Test]
    public void An_empty_account_a_takeover_touches_is_removed_with_its_storage_before_the_next_takeover_pays_it()
    {
        Assert.That(StateReader.TryGetAccount(Parent, EmptyWithStorage, out AccountStruct before) && before.IsEmpty && before.HasStorage, Is.True,
            "precondition: an empty account with storage");
        // The touch removes the account (EIP-161), so the payment after it creates the account anew, without storage.
        Block block = BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Counter),
            Transfer(TestItem.PrivateKeyB, 0, EmptyWithStorage, 0),
            Transfer(TestItem.PrivateKeyD, 0, EmptyWithStorage, 1.Wei),
            Call(TestItem.PrivateKeyC, 0, Logger));

        Run run = Handoff(block);

        bool exists = StateReader.TryGetAccount(Processed(block, run.StateRoot), EmptyWithStorage, out AccountStruct after);
        using (Assert.EnterMultipleScope())
        {
            // The counter's commit is shared with the touch, which is committed at once; the payment's with the last.
            Assert.That((run.Tally.Replayed, Shared), Is.EqualTo((4, 2)));
            Assert.That((exists, after.Balance, after.HasStorage), Is.EqualTo((true, (UInt256)1, false)));
        }
    }

    [Test]
    public void A_takeover_destroying_a_contract_starts_from_the_commit_of_the_takeovers_before_it()
    {
        Run run = Handoff(BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Counter),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 1.Wei),
            Call(TestItem.PrivateKeyD, 0, Child),
            Call(TestItem.PrivateKeyA, 1, Logger),
            Call(TestItem.PrivateKeyB, 1, Child, data: [1])));

        // Shanghai deletes the contract with its storage, which the last call reads; from Cancun it only pays its balance.
        Assert.That((run.Tally.Replayed, Shared), Is.EqualTo(Spec.IsEip6780Enabled ? (5, 4) : (4, 3)));
    }

    [Test]
    public void The_last_takeover_of_a_block_commits_and_no_commit_covers_more_than_the_most_it_may([Values(1, 2, DefaultMaxTxs)] int maxTxs)
    {
        try
        {
            if (maxTxs != _maxTxs) Reinitialize(sharing: true, maxTxs);
            Run run = Handoff(BuildBlock(
                Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressC, 1.Wei),
                Call(TestItem.PrivateKeyB, 0, Logger),
                Transfer(TestItem.PrivateKeyC, 0, TestItem.AddressD, 1.Wei),
                Call(TestItem.PrivateKeyD, 0, Reverter),
                Transfer(TestItem.PrivateKeyA, 1, TestItem.AddressB, 1.Wei)));

            Assert.That((run.Tally.Replayed, Shared), Is.EqualTo((5, maxTxs switch { 1 => 0, 2 => 2, _ => 4 })));
        }
        finally
        {
            _maxTxs = DefaultMaxTxs;
        }
    }

    [Test]
    public void Switched_off_every_takeover_commits_on_its_own()
    {
        try
        {
            Reinitialize(sharing: false, DefaultMaxTxs);
            Run run = Handoff(BuildBlock(
                Call(TestItem.PrivateKeyA, 0, Counter),
                Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 1.Wei),
                Call(TestItem.PrivateKeyD, 0, Logger)));

            Assert.That((run.Tally.Replayed, Shared), Is.EqualTo((3, 0)));
        }
        finally
        {
            _sharing = true;
        }
    }

    /// <summary>
    /// The world state's check starts at the first change after a takeover's snapshot, and starting it one change later
    /// would change no block's result: that change is always the sender's, as a run's first effect is its sender's gas
    /// debit or nonce and the run sets the sender's nonce, which leaves the change not empty. This pins that order over
    /// the randomized blocks and every transaction kind of the fork.
    /// </summary>
    [Test]
    public void Every_run_starts_with_its_senders_debit_or_nonce_and_sets_the_senders_nonce()
    {
        List<Block> blocks = [];
        for (int i = 0; i < 60; i++) blocks.Add(RandomBlock(new Random(0x4e4d_0c0d + i)));
        List<Transaction> kinds =
        [
            AccessListCall(TestItem.PrivateKeyA, 0, Counter),
            LegacyCall(TestItem.PrivateKeyB, 0, Logger),
            Create(TestItem.PrivateKeyC, 0, DeployCode),
            Transfer(TestItem.PrivateKeyD, 0, EmptyWithStorage, 0),
        ];
        if (Spec.IsEip4844Enabled) kinds.Add(BlobCall(TestItem.PrivateKeyD, 1, Logger));
        if (Spec.IsEip7702Enabled) kinds.Add(SetCodeCall(TestItem.PrivateKeyD, 2, TestItem.PrivateKeyE, Counter));
        blocks.Add(BuildBlock([.. kinds]));

        int runs = 0;
        List<string> wrong = [];
        foreach (Block block in blocks)
        {
            RunPreWarmCaches(PreWarmer, block);
            // A block too small to warm records nothing.
            if (PreWarmer.Footprints is not { } footprints) continue;
            for (int i = 0; i < footprints.Count; i++)
            {
                if (footprints.Get(i) is not { } footprint) continue;
                runs++;
                Address sender = footprint.Transaction.SenderAddress!;
                ReadOnlySpan<StateEffect> effects = footprint.Effects;
                bool startsWithSender = effects.Length > 0 && effects[0].Address == sender
                    && effects[0].Kind is EffectKind.SubtractFromBalance or EffectKind.AddToBalance or EffectKind.SetNonce;
                bool setsNonce = false;
                foreach (ref readonly StateEffect effect in effects)
                {
                    setsNonce |= effect.Kind == EffectKind.SetNonce && effect.Address == sender;
                }

                if (!startsWithSender || !setsNonce) wrong.Add($"block {block.Number} tx {i}: first effect {(effects.Length > 0 ? $"{effects[0].Kind} of {effects[0].Address}" : "none")}, sets the nonce {setsNonce}");
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(wrong, Is.Empty);
            Assert.That(runs, Is.GreaterThan(300), "precondition: runs were recorded");
        }
    }

    /// <summary>Randomized blocks of balance, nonce and slot changes, touches, payments, logs and destructions.</summary>
    [Test]
    public void Randomized_blocks_end_in_the_state_and_receipts_of_executing_them()
    {
        const int seed = 0x4e4d_0c0d;
        const int blocks = 400;
        int shared = 0;
        int replayed = 0;
        for (int i = 0; i < blocks; i++)
        {
            int blockSeed = seed + i;
            int before = Shared;
            try
            {
                replayed += Handoff(RandomBlock(new Random(blockSeed))).Tally.Replayed;
            }
            catch (Exception)
            {
                TestContext.Out.WriteLine($"Failing block seed {blockSeed} (block {i} of {blocks}, {Spec.Name})");
                throw;
            }

            shared += Shared - before;
        }

        TestContext.Out.WriteLine($"{blocks} blocks, {Spec.Name}: {replayed} takeovers, {shared} shared commits");
        Assert.That(shared, Is.GreaterThan(blocks), "precondition: takeovers shared commits");
    }

    private Block RandomBlock(Random rng)
    {
        PrivateKey[] senders = [TestItem.PrivateKeyA, TestItem.PrivateKeyB, TestItem.PrivateKeyC, TestItem.PrivateKeyD];
        ulong[] nonces = new ulong[senders.Length];
        Transaction[] transactions = new Transaction[rng.Next(2, 14)];
        for (int i = 0; i < transactions.Length; i++)
        {
            int s = rng.Next(senders.Length);
            PrivateKey sender = senders[s];
            ulong nonce = nonces[s]++;
            transactions[i] = rng.Next(12) switch
            {
                0 => Transfer(sender, nonce, EmptyWithStorage, 0),
                1 => Transfer(sender, nonce, EmptyWithStorage, 1.Wei),
                2 => Transfer(sender, nonce, TestItem.AddressC, 1.Wei),
                3 => Call(sender, nonce, BalanceReader),
                4 => Call(sender, nonce, Counter),
                5 => Call(sender, nonce, Logger),
                6 => Call(sender, nonce, Child, data: rng.Next(2) == 0 ? null : [1]),
                7 => Call(sender, nonce, Spec.IsEip1153Enabled ? Transient : Reverter),
                8 => Transfer(sender, nonce, Fresh, 1.Wei),
                9 => Call(sender, nonce, Gift),
                10 => Call(sender, nonce, Payer),
                _ => Call(sender, nonce, Copier, data: rng.Next(2) == 0 ? null : [1]),
            };
        }

        return BuildBlock(transactions);
    }

    private void Reinitialize(bool sharing, int maxTxs)
    {
        TearDown();
        _sharing = sharing;
        _maxTxs = maxTxs;
        Setup();
    }
}
