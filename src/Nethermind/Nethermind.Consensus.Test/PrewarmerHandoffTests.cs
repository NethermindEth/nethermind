// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Threading;
using Autofac;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Threading;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

[TestFixtureSource(nameof(Forks))]
public class PrewarmerHandoffTests(IReleaseSpec spec) : PrewarmerHandoffTestBase(spec)
{
    // Shanghai still deletes a self-destructed account; Cancun only does for one created in the same transaction.
    private static readonly IReleaseSpec[] Forks = [Osaka.Instance, Shanghai.Instance];

    [Test]
    public void A_block_of_mixed_transactions_ends_in_the_state_and_receipts_of_executing_it()
    {
        (int replayed, int rejected, _) = Handoff(BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Counter),
            Call(TestItem.PrivateKeyB, 0, Counter),
            Transfer(TestItem.PrivateKeyA, 1, TestItem.AddressC, 1.Wei),
            Transfer(TestItem.PrivateKeyB, 1, TestItem.AddressC, 5.Wei),
            Call(TestItem.PrivateKeyA, 2, Reverter),
            Call(TestItem.PrivateKeyB, 2, Logger),
            Create(TestItem.PrivateKeyC, 0, DeployCode),
            Call(TestItem.PrivateKeyD, 0, BalanceReader))).Tally;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(replayed, Is.GreaterThan(0));
            Assert.That(rejected, Is.GreaterThan(0));
        }
    }

    [Test]
    public void Payments_to_one_account_replay_while_a_read_of_its_balance_does_not()
    {
        (int replayed, int rejected, _) = Handoff(BuildBlock(
            Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressC, 1.Wei),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 2.Wei),
            Transfer(TestItem.PrivateKeyD, 0, TestItem.AddressC, 3.Wei),
            Call(TestItem.PrivateKeyA, 1, BalanceReader))).Tally;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(replayed, Is.EqualTo(3));
            Assert.That(rejected, Is.EqualTo(1));
        }
    }

    [Test]
    public void A_senders_later_transaction_replays_after_its_earlier_one_was_executed()
    {
        (int replayed, int rejected, _) = Handoff(BuildBlock(
            Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressC, 1.Wei),
            Call(TestItem.PrivateKeyB, 0, BalanceReader),
            Transfer(TestItem.PrivateKeyB, 1, TestItem.AddressD, 1.Wei))).Tally;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(replayed, Is.EqualTo(2));
            Assert.That(rejected, Is.EqualTo(1));
        }
    }

    [Test]
    public void Transactions_that_read_a_slot_an_earlier_one_writes_are_refreshed_and_taken_over()
    {
        (int replayed, int rejected, _) = Handoff(BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Counter),
            Call(TestItem.PrivateKeyB, 0, Counter),
            Call(TestItem.PrivateKeyD, 0, Counter))).Tally;

        Assert.That((replayed, rejected), Is.EqualTo((3, 0)));
    }

    [Test]
    public void A_heavy_senders_transactions_warmed_apart_replay()
    {
        (int replayed, _, _) = Handoff(BuildBlock(
            Transfer(TestItem.PrivateKeyC, 0, TestItem.AddressA, 1.Wei, gasLimit: 2_000_000),
            Transfer(TestItem.PrivateKeyC, 1, TestItem.AddressB, 1.Wei, gasLimit: 2_000_000),
            Transfer(TestItem.PrivateKeyC, 2, TestItem.AddressD, 1.Wei, gasLimit: 2_000_000))).Tally;

        Assert.That(replayed, Is.EqualTo(3));
    }

    [Test]
    public void Value_sent_by_a_contract_whose_balance_the_block_changed_replays_while_it_can_pay([Values] bool fundedForAll)
    {
        Address payer = fundedForAll ? Payer : ScarcePayer;
        (int replayed, int rejected, _) = Handoff(BuildBlock(
            Call(TestItem.PrivateKeyA, 0, payer),
            Call(TestItem.PrivateKeyB, 0, payer),
            Call(TestItem.PrivateKeyD, 0, payer))).Tally;

        Assert.That((replayed, rejected), Is.EqualTo(fundedForAll ? (3, 0) : (1, 2)));
    }

    [Test]
    public void A_write_undone_by_a_reverted_inner_call_stays_undone()
    {
        (int replayed, _, _) = Handoff(BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Caller),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 1.Wei),
            Transfer(TestItem.PrivateKeyD, 0, TestItem.AddressC, 1.Wei))).Tally;

        Assert.That(replayed, Is.EqualTo(3));
    }

    [Test]
    public void A_value_call_to_an_account_the_block_brought_to_life_is_executed()
    {
        (int replayed, int rejected, _) = Handoff(BuildBlock(
            Transfer(TestItem.PrivateKeyA, 0, Fresh, 1.Wei),
            Call(TestItem.PrivateKeyB, 0, Gift),
            Transfer(TestItem.PrivateKeyD, 0, TestItem.AddressC, 1.Wei))).Tally;

        Assert.That((replayed, rejected), Is.EqualTo((2, 1)));
    }

    [Test]
    public void A_sender_funded_earlier_in_the_block_is_executed()
    {
        PrivateKey unfunded = TestItem.PrivateKeyE;
        (_, _, int missing) = Handoff(BuildBlock(
            Transfer(TestItem.PrivateKeyA, 0, unfunded.Address, 1.Ether),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 1.Wei),
            Transfer(unfunded, 0, TestItem.AddressD, 1.Wei))).Tally;

        Assert.That(missing, Is.GreaterThan(0));
    }

    [Test]
    public void A_destroyed_contract_with_storage_ends_in_the_state_of_executing_it([Values] bool redeployed)
    {
        (int replayed, _, _) = Handoff(BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Child),
            redeployed ? Call(TestItem.PrivateKeyB, 0, Factory, gasLimit: 300_000) : Call(TestItem.PrivateKeyB, 0, Counter),
            Call(TestItem.PrivateKeyD, 0, Child, data: [1]))).Tally;

        Assert.That(replayed, Is.EqualTo(Spec.IsEip6780Enabled ? 3 : redeployed ? 1 : 2));
    }

    [Test]
    public void A_contract_destroyed_by_a_replay_has_none_of_its_storage_when_deployed_again_later()
    {
        Hash256 executed = DestroyThenRedeploy(handoff: false);
        TearDown();
        Setup();
        Hash256 replayed = DestroyThenRedeploy(handoff: true);

        Assert.That(replayed, Is.EqualTo(executed));
    }

    [Test]
    public void Contracts_created_and_destroyed_within_their_transactions_end_in_the_state_of_executing_them()
    {
        (int replayed, _, _) = Handoff(BuildBlock(
            Call(TestItem.PrivateKeyA, 0, FreshFactory, gasLimit: 300_000, data: [1]),
            Call(TestItem.PrivateKeyB, 0, FreshFactory, gasLimit: 300_000, data: [1]),
            Call(TestItem.PrivateKeyD, 0, FreshFactory, gasLimit: 300_000))).Tally;

        // The later deployments read the factory's nonce, which the first one increments.
        Assert.That(replayed, Is.EqualTo(1));
    }

    [Test]
    public void Transactions_of_every_kind_the_fork_has_replay_into_the_state_and_receipts_of_executing_them()
    {
        List<Transaction> transactions =
        [
            AccessListCall(TestItem.PrivateKeyA, 0, Counter),
            LegacyCall(TestItem.PrivateKeyB, 0, Logger),
            LegacyCall(TestItem.PrivateKeyB, 1, Reverter),
        ];
        if (Spec.IsEip1153Enabled) transactions.Add(Call(TestItem.PrivateKeyC, 0, Transient));
        if (Spec.IsEip4844Enabled) transactions.Add(BlobCall(TestItem.PrivateKeyD, 0, Logger));
        if (Spec.IsEip7702Enabled) transactions.Add(SetCodeCall(TestItem.PrivateKeyD, 1, TestItem.PrivateKeyE, Counter));

        (int replayed, _, _) = Handoff(BuildBlock([.. transactions])).Tally;

        Assert.That(replayed, Is.EqualTo(transactions.Count));
    }

    [Test]
    public void A_value_transfer_to_a_precompile_replays_until_the_block_creates_its_account()
    {
        (int replayed, int rejected, _) = Handoff(BuildBlock(
            Transfer(TestItem.PrivateKeyB, 0, Ripemd, 1.Wei, gasLimit: 50_000),
            Transfer(TestItem.PrivateKeyC, 0, Ripemd, 1.Wei, gasLimit: 50_000),
            Transfer(TestItem.PrivateKeyD, 0, TestItem.AddressA, 1.Wei))).Tally;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(replayed, Is.EqualTo(2));
            Assert.That(rejected, Is.EqualTo(1));
        }
    }

    [Test]
    public void An_invalid_transaction_is_executed_rather_than_replayed([Values] bool senderHasCode)
    {
        Block block = senderHasCode
            ? BuildBlock(
                Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressC, 1.Wei),
                Transfer(CodeOwner, 0, TestItem.AddressD, 1.Wei),
                Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressD, 1.Wei))
            : BuildBlock(Parent, 0x9c9a,
                Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressC, 1.Wei),
                Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressD, 1.Wei),
                Transfer(TestItem.PrivateKeyC, 0, TestItem.AddressD, 1.Wei));

        Run run = Handoff(block);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((bool)run.Results[1], Is.False);
            Assert.That(run.Tally.Replayed, Is.EqualTo(senderHasCode ? 2 : 1));
        }
    }

    private Hash256 DestroyThenRedeploy(bool handoff)
    {
        Block destroy = BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Child),
            Call(TestItem.PrivateKeyB, 0, Counter),
            Call(TestItem.PrivateKeyD, 0, Logger));
        Hash256 root;
        if (handoff)
        {
            RunPreWarmCaches(PreWarmer, destroy);
            Run run = Process(destroy, ProductionAdapter);
            Assert.That(run.Tally.Replayed, Is.EqualTo(3));
            root = run.StateRoot;
        }
        else
        {
            root = Process(destroy, adapter: null).StateRoot;
        }

        BlockHeader first = Processed(destroy, root);
        Block redeploy = BuildBlock(first, Call(TestItem.PrivateKeyB, 1, Factory, gasLimit: 300_000));
        BlockHeader second = Processed(redeploy, Process(redeploy, adapter: null, first).StateRoot);
        return Process(BuildBlock(second, Call(TestItem.PrivateKeyD, 1, Child, data: [1])), adapter: null, second).StateRoot;
    }
}

[TestFixture]
public class PrewarmerHandoffMechanicsTests() : PrewarmerHandoffTestBase(Osaka.Instance)
{
    [Test]
    public void Footprints_recorded_for_another_block_are_not_replayed([Values] bool sameTransactions)
    {
        Block other = ThreeIndependentTransactions();
        Block block = sameTransactions ? BuildBlock(Parent, 30_020_045, other.Transactions) : ThreeIndependentTransactions();

        RunPreWarmCaches(PreWarmer, other);

        Assert.That(Process(block, ProductionAdapter).Tally.Replayed, Is.Zero);
    }

    [Test]
    public void A_block_traced_beyond_its_receipts_is_executed()
    {
        Run run = Handoff(ThreeIndependentTransactions(), new GethLikeBlockMemoryTracer(GethTraceOptions.Default));

        Assert.That(run.Tally.Replayed, Is.Zero);
    }

    [Test]
    public void Switched_off_it_leaves_every_transaction_to_execution()
    {
        TearDown();
        Initialize(handoff: false);

        Assert.That(Handoff(ThreeIndependentTransactions()).Tally, Is.EqualTo((0, 0, 0)));
    }

    [Test]
    public void A_world_state_decorated_outside_the_recorder_still_hands_off()
    {
        PreBlockCaches caches = ProcessingScope.Resolve<PreBlockCaches>();
        using BlockCachePreWarmer preWarmer = new(
            new OuterDecoratedEnvs(ProcessingScope.Resolve<PrewarmerEnvFactory>(), caches),
            minPoolSize: 4,
            concurrency: 3,
            parallelExecutionBatchRead: true,
            ProcessingScope.Resolve<NodeStorageCache>(),
            caches,
            LimboLogs.Instance,
            handoff: true);
        PrewarmerTxAdapter adapter = new(
            new ExecuteTransactionProcessorAdapter(ProcessingScope.Resolve<ITransactionProcessor>()),
            preWarmer,
            new PrewarmerState(caches, isPrewarmer: false),
            ProcessingScope.Resolve<IWorldState>(),
            LimboLogs.Instance);

        Assert.That(Handoff(ThreeIndependentTransactions(), preWarmer, adapter).Tally.Replayed, Is.EqualTo(3));
    }

    [Test]
    public void Blocks_whose_gas_or_access_list_is_built_while_executing_are_not_recorded()
    {
        Block block = ThreeIndependentTransactions();
        ReleaseSpec withStateGas = ((ReleaseSpec)Spec).Clone();
        withStateGas.IsEip8037Enabled = true;
        ReleaseSpec withAccessLists = ((ReleaseSpec)Spec).Clone();
        withAccessLists.IsEip7928Enabled = true;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(BlockFootprints.AppliesTo(block, Spec), Is.True);
            Assert.That(BlockFootprints.AppliesTo(block, withStateGas), Is.False);
            Assert.That(BlockFootprints.AppliesTo(block, withAccessLists), Is.False);
            Assert.That(BlockFootprints.AppliesTo(BuildBlock(), Spec), Is.False);
        }
    }

    [Test]
    public void Transactions_whose_checks_a_warm_run_skips_for_good_are_not_recorded()
    {
        Transaction free = Build.A.Transaction.WithType(TxType.EIP1559).WithMaxFeePerGas(0).WithMaxPriorityFeePerGas(0)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction lastNonce = Build.A.Transaction.WithType(TxType.EIP1559).WithNonce(ulong.MaxValue)
            .WithMaxFeePerGas(2.GWei).WithMaxPriorityFeePerGas(1.GWei).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction system = Build.A.Transaction.WithSenderAddress(Address.SystemUser).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(BlockFootprints.IsRecordable(Call(TestItem.PrivateKeyA, 0, Counter)), Is.True);
            Assert.That(BlockFootprints.IsRecordable(free), Is.False);
            Assert.That(BlockFootprints.IsRecordable(lastNonce), Is.False);
            Assert.That(BlockFootprints.IsRecordable(system), Is.False);
        }
    }

    [Test]
    [NonParallelizable]
    public void Taken_over_transactions_count_in_the_execution_counters_like_executed_ones()
    {
        Block block = BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Counter),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 1.Wei),
            Call(TestItem.PrivateKeyC, 0, Caller),
            Create(TestItem.PrivateKeyD, 0, DeployCode));

        long[] executed = MainThreadCounts(() => Process(block, adapter: null));
        RunPreWarmCaches(PreWarmer, block);
        Run run = null!;
        long[] takenOver = MainThreadCounts(() => run = Process(block, ProductionAdapter));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Tally.Replayed, Is.EqualTo(4));
            Assert.That(takenOver, Is.EqualTo(executed));
            Assert.That(executed, Has.All.GreaterThan(0));
        }

        static long[] MainThreadCounts(Action process)
        {
            bool wasProcessing = ProcessingThread.IsBlockProcessingThread;
            ProcessingThread.IsBlockProcessingThread = true;
            try
            {
                long[] before = Read();
                process();
                long[] after = Read();
                return [.. after.Select((value, i) => value - before[i])];
            }
            finally
            {
                ProcessingThread.IsBlockProcessingThread = wasProcessing;
            }
        }

        static long[] Read() =>
        [
            Evm.Metrics.MainThreadOpCodes, Evm.Metrics.MainThreadSLoadOpcode, Evm.Metrics.MainThreadSStoreOpcode,
            Evm.Metrics.MainThreadCalls, Evm.Metrics.MainThreadEmptyCalls, Evm.Metrics.MainThreadCreates
        ];
    }

    [Test]
    public void Only_envs_on_the_ethereum_transaction_processor_record()
    {
        PrewarmerEnvFactory Factory(ITransactionProcessor processor) =>
            new(ProcessingScope.Resolve<IWorldStateManager>(), LimboLogs.Instance, ProcessingScope, new BlocksConfig(), processor);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Factory(ProcessingScope.Resolve<ITransactionProcessor>()).RecordsFootprints, Is.True);
            Assert.That(Factory(Substitute.For<ITransactionProcessor>()).RecordsFootprints, Is.False);
        }
    }

    [Test]
    public void The_recorder_answers_reads_the_interface_derives_from_a_whole_account()
    {
        InterfaceMapping map = typeof(FootprintRecorder).GetInterfaceMap(typeof(IAccountStateProvider));
        int hasCode = Array.FindIndex(map.InterfaceMethods, m => m.Name == nameof(IAccountStateProvider.HasCode));
        Assert.That(map.TargetMethods[hasCode].DeclaringType, Is.EqualTo(typeof(FootprintRecorder)));
    }

    [Test]
    public void The_writes_of_a_transaction_block_processing_executes_reach_the_footprints()
    {
        UInt256 paidTo;
        IWorldState worldState = ProcessingScope.Resolve<IWorldState>();
        using (worldState.BeginScope(Parent)) paidTo = worldState.GetBalance(TestItem.AddressC);
        Block block = BuildBlock(
            Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressC, 0x4e4d),
            Call(TestItem.PrivateKeyB, 0, BalanceReader),
            Transfer(TestItem.PrivateKeyD, 0, TestItem.AddressA, 1.Wei));

        RunPreWarmCaches(PreWarmer, block);
        Assert.That(Process(block, ProductionAdapter).Tally.Rejected, Is.EqualTo(1), "the balance read is executed");

        BlockFootprints footprints = PreWarmer.Footprints!;
        footprints.ApplyExecuted();
        Assert.That(footprints.ValueBefore(new StorageCell(BalanceReader, 0), 2), Is.EqualTo(paidTo + 0x4e4d));
    }

    [Test]
    public void Footprints_block_processing_invalidates_while_warming_are_refreshed_and_taken_over([Values] bool writes)
    {
        UInt256 paidTo;
        IWorldState worldState = ProcessingScope.Resolve<IWorldState>();
        using (worldState.BeginScope(Parent)) paidTo = worldState.GetBalance(TestItem.AddressC);
        UInt256 paid = writes ? (UInt256)(2 * 0x4e4d) : 0x4e4d;
        // The transaction after the one block processing executes is left to execution, so the copy comes one later.
        Block block = BuildBlock(
            Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressC, paid),
            Call(TestItem.PrivateKeyB, 0, Copier),
            Transfer(TestItem.PrivateKeyD, 0, TestItem.AddressA, 1.Wei),
            Call(TestItem.PrivateKeyD, 1, Copier, data: [1]));
        StorageCell copied = new(Copier, 0);
        UInt256? left = writes ? paidTo + paid : null;

        Run executed = Process(block, adapter: null);
        Run run = ProcessWhileWarming(block, (footprints, index) => index switch
        {
            // Warmed, and the copy refreshed on the write the second transaction's footprint predicts.
            0 => footprints.WarmPassEnded && !footprints.HasWork && Enumerable.Range(0, footprints.Count).All(i => footprints.Get(i) is not null)
                && ReadOf(footprints.Get(3)!, copied) == paidTo,
            2 => footprints.ValueBefore(copied, 3) == left && !footprints.HasWork && footprints.Get(3) is { } footprint
                && ReadOf(footprint, copied) == (left ?? UInt256.Zero),
            _ => true
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That((run.Tally.Replayed, run.Tally.Rejected), Is.EqualTo((3, 1)));
            Assert.That(run.Results, Is.EqualTo(executed.Results));
            Assert.That(run.StateRoot, Is.EqualTo(executed.StateRoot));
        }

        AssertSameReceipts(run.Receipts, executed.Receipts);
    }

    [Test]
    public void Readers_of_a_write_no_longer_made_are_invalidated([Values] bool executed, [Values] bool writesAnother)
    {
        (BlockFootprints footprints, Transaction[] txs) = Footprints(3);
        StorageCell cell = new(TestItem.AddressC, 0x4e4d);
        StorageCell another = new(TestItem.AddressC, 2 * 0x4e4d);
        footprints.Store(0, Footprint(txs[0], writes: [(cell, 0x4e4d)]));
        footprints.Store(2, Footprint(txs[2], reads: [(cell, 0x4e4d)]));
        Assert.That(footprints.TryTakeInvalidated(-1, out _), Is.False);

        (StorageCell Cell, UInt256 Value)[] writes = writesAnother ? [(another, 0x4e4d)] : [];
        if (executed)
        {
            footprints.QueueExecuted(0, writesAnother ? [.. writes] : null);
            footprints.ApplyExecuted();
        }
        else
        {
            footprints.Store(0, Footprint(txs[0], writes: writes));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(footprints.ValueBefore(cell, 2), Is.Null);
            Assert.That(footprints.TryTakeInvalidated(-1, out int position), Is.True);
            Assert.That(position, Is.EqualTo(2));
        }
    }

    [Test]
    public void A_footprint_writing_a_slot_twice_leaves_its_last_write()
    {
        (BlockFootprints footprints, Transaction[] txs) = Footprints(2);
        StorageCell cell = new(TestItem.AddressC, 0x4e4d);
        footprints.Store(0, Footprint(txs[0], writes: [(cell, 0x4e4d), (cell, 2 * 0x4e4d)]));

        Assert.That(footprints.ValueBefore(cell, 1), Is.EqualTo((UInt256)(2 * 0x4e4d)));
    }

    [Test]
    public void A_footprint_stored_after_its_transaction_was_executed_leaves_the_executed_writes()
    {
        (BlockFootprints footprints, Transaction[] txs) = Footprints(2);
        StorageCell cell = new(TestItem.AddressC, 0x4e4d);
        footprints.QueueExecuted(0, [(cell, 2 * 0x4e4d)]);
        footprints.ApplyExecuted();

        footprints.Store(0, Footprint(txs[0], writes: [(cell, 0x4e4d)]));

        Assert.That(footprints.ValueBefore(cell, 1), Is.EqualTo((UInt256)(2 * 0x4e4d)));
    }

    [Test]
    public void Writes_block_processing_reports_invalidate_the_footprints_that_read_other_values([Values] bool sameValue)
    {
        (BlockFootprints footprints, Transaction[] txs) = Footprints(3);
        StorageCell cell = new(TestItem.AddressC, 0x4e4d);
        footprints.Store(2, Footprint(txs[2], reads: [(cell, 0x4e4d)]));

        footprints.QueueExecuted(0, [(cell, sameValue ? (UInt256)0x4e4d : 2 * 0x4e4d)]);
        Assert.That(footprints.TryTakeInvalidated(-1, out _), Is.False, "queued writes count once they are applied");
        footprints.ApplyExecuted();

        Assert.That(footprints.TryTakeInvalidated(-1, out int position), Is.EqualTo(!sameValue));
        if (!sameValue) Assert.That(position, Is.EqualTo(2));
    }

    [Test]
    public void A_footprint_reading_what_an_earlier_one_writes_otherwise_is_invalidated_once_both_are_stored([Values] bool readerFirst)
    {
        (BlockFootprints footprints, Transaction[] txs) = Footprints(2);
        StorageCell cell = new(TestItem.AddressC, 0x4e4d);
        TransactionFootprint writer = Footprint(txs[0], writes: [(cell, 2 * 0x4e4d)]);
        TransactionFootprint reader = Footprint(txs[1], reads: [(cell, 0x4e4d)]);

        footprints.Store(readerFirst ? 1 : 0, readerFirst ? reader : writer);
        footprints.Store(readerFirst ? 0 : 1, readerFirst ? writer : reader);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(footprints.TryTakeInvalidated(-1, out int position), Is.True);
            Assert.That(position, Is.EqualTo(1));
            Assert.That(footprints.ValueBefore(cell, 1), Is.EqualTo((UInt256)(2 * 0x4e4d)));
        }
    }

    [Test]
    public void Invalidated_footprints_block_processing_has_reached_are_dropped()
    {
        (BlockFootprints footprints, Transaction[] txs) = Footprints(3);
        StorageCell cell = new(TestItem.AddressC, 0x4e4d);
        footprints.Store(0, Footprint(txs[0], writes: [(cell, 2 * 0x4e4d)]));
        footprints.Store(1, Footprint(txs[1], reads: [(cell, 0x4e4d)]));
        footprints.Store(2, Footprint(txs[2], reads: [(cell, 0x4e4d)]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(footprints.TryTakeInvalidated(1, out int position), Is.True);
            Assert.That(position, Is.EqualTo(2));
            Assert.That(footprints.TryTakeInvalidated(-1, out _), Is.False);
        }
    }

    [Test]
    public void A_slot_too_many_transactions_write_is_no_longer_tracked()
    {
        int writers = BlockFootprints.MaxIndexedPerSlot + 1;
        (BlockFootprints footprints, Transaction[] txs) = Footprints(writers + 1);
        StorageCell cell = new(TestItem.AddressC, 0x4e4d);
        for (int i = 0; i < writers; i++) footprints.Store(i, Footprint(txs[i], writes: [(cell, (UInt256)(i + 1))]));
        TransactionFootprint reader = Footprint(txs[writers], reads: [(cell, 0x4e4d)]);
        footprints.Store(writers, reader);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(footprints.TryTakeInvalidated(-1, out _), Is.False);
            Assert.That(footprints.ReadsUntrackedSlot(reader), Is.True);
            Assert.That(footprints.ValueBefore(cell, writers), Is.Null);
        }
    }

    [Test]
    public void A_slot_too_many_transactions_read_keeps_its_writes_tracked()
    {
        int readers = BlockFootprints.MaxIndexedPerSlot + 1;
        (BlockFootprints footprints, Transaction[] txs) = Footprints(readers + 1);
        StorageCell read = new(TestItem.AddressC, 0x4e4d);
        StorageCell written = new(TestItem.AddressC, 2 * 0x4e4d);
        footprints.Store(0, Footprint(txs[0], writes: [(written, 0x4e4d)]));
        for (int i = 1; i < readers; i++) footprints.Store(i, Footprint(txs[i], reads: [(read, UInt256.Zero)]));
        TransactionFootprint last = Footprint(txs[readers], reads: [(read, UInt256.Zero), (written, UInt256.Zero)]);
        footprints.Store(readers, last);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(footprints.TryTakeInvalidated(-1, out int position), Is.True);
            Assert.That(position, Is.EqualTo(readers));
            Assert.That(footprints.ReadsUntrackedSlot(last), Is.False);
        }
    }

    [Test]
    public void A_block_runs_at_most_its_refreshes()
    {
        const int notRun = 4;
        int readers = BlockFootprints.MaxRefreshesPerBlock + 2 * notRun;
        (BlockFootprints footprints, Transaction[] txs) = Footprints(readers + 1);
        footprints.Store(0, Footprint(txs[0], writes: [.. Enumerable.Range(0, readers).Select(static i => (new StorageCell(TestItem.AddressC, (UInt256)i), (UInt256)0x4e4d))]));
        for (int i = 1; i <= readers; i++) footprints.Store(i, Footprint(txs[i], reads: [(new StorageCell(TestItem.AddressC, (UInt256)(i - 1)), UInt256.Zero)]));

        int taken = 0;
        while (footprints.TryTakeInvalidated(-1, out _))
        {
            if (taken++ >= notRun) footprints.CountRefresh();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(taken, Is.EqualTo(BlockFootprints.MaxRefreshesPerBlock + notRun));
            Assert.That(footprints.HasWork, Is.False);
        }
    }

    private static (BlockFootprints Footprints, Transaction[] Transactions) Footprints(int count)
    {
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++) txs[i] = Build.A.Transaction.WithNonce((ulong)i).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        return (new BlockFootprints(Build.A.Block.WithTransactions(txs).TestObject), txs);
    }

    private static UInt256? ReadOf(TransactionFootprint footprint, in StorageCell cell)
    {
        foreach (ref readonly SlotPrecondition slot in footprint.Slots)
        {
            if (slot.Cell.Equals(cell)) return slot.Value;
        }

        return null;
    }

    private static TransactionFootprint Footprint(Transaction tx, (StorageCell Cell, UInt256 Value)[]? reads = null, (StorageCell Cell, UInt256 Value)[]? writes = null) =>
        new(tx, [],
            [.. (reads ?? []).Select(static read => new SlotPrecondition { Cell = read.Cell, Value = read.Value, Read = true })],
            [.. (writes ?? []).Select(static write => new StateEffect { Kind = EffectKind.SetStorage, Address = write.Cell.Address, Index = write.Cell.Index, Value = write.Value })],
            default, default, default);

    [Test]
    public void A_run_stops_once_block_processing_starts_its_transaction_and_is_undone()
    {
        IWorldState worldState = ProcessingScope.Resolve<IWorldState>();
        using (worldState.BeginScope(Parent))
        {
            FootprintRecorder recorder = new(worldState);
            Progress progress = new() { MainThreadTxIndex = 1 };
            UInt256 before = worldState.GetBalance(TestItem.AddressC);

            recorder.Start(progress, txIndex: 2, CancellationToken.None);
            recorder.AddToBalance(TestItem.AddressC, 0x4e4d, Spec, out _);
            ITxTracer outcome = recorder.Outcome;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(outcome.IsCancelable, Is.True);
                Assert.That(outcome.IsCancelled, Is.False);
                progress.MainThreadTxIndex = 2;
                Assert.That(outcome.IsCancelled, Is.True);
            }

            recorder.Discard();
            Assert.That(worldState.GetBalance(TestItem.AddressC), Is.EqualTo(before));
        }
    }

    private Block ThreeIndependentTransactions() => BuildBlock(
        Call(TestItem.PrivateKeyA, 0, Counter),
        Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 1.Wei),
        Call(TestItem.PrivateKeyD, 0, Logger));

    private sealed class Progress : IBlockProcessingProgress
    {
        public int MainThreadTxIndex { get; set; }
    }

    /// <summary>Envs whose scopes expose their world state through one more decorator than the recorder.</summary>
    private sealed class OuterDecoratedEnvs(PrewarmerEnvFactory factory, PreBlockCaches caches) : IPooledObjectPolicy<IPrewarmerEnv>
    {
        public IPrewarmerEnv Create() => new Env(factory.Create(caches));

        public bool Return(IPrewarmerEnv obj) => true;

        private sealed class Env(IPrewarmerEnv inner) : IPrewarmerEnv
        {
            public ReadOnlySpan<IHasAccessList> SystemAccessLists => inner.SystemAccessLists;

            FootprintRecorder? IPrewarmerEnv.Recorder => inner.Recorder;

            public bool TryBuild(BlockHeader? baseBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope)
            {
                scope = inner.TryBuild(baseBlock, out IReadOnlyTxProcessingScope? built) ? new Scope(built) : null;
                return scope is not null;
            }

            public bool TryBuildAtTarget(BlockHeader targetBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope)
            {
                scope = inner.TryBuildAtTarget(targetBlock, out IReadOnlyTxProcessingScope? built) ? new Scope(built) : null;
                return scope is not null;
            }

            public void Dispose() => inner.Dispose();
        }

        private sealed class Scope(IReadOnlyTxProcessingScope inner) : IReadOnlyTxProcessingScope
        {
            public ITransactionProcessor TransactionProcessor => inner.TransactionProcessor;
            public IWorldState WorldState { get; } = new Forwarding(inner.WorldState);
            public void Reset() => inner.Reset();
            public void Dispose() => inner.Dispose();
        }

        private sealed class Forwarding(IWorldState state) : WorldStateDecorator(state);
    }
}

public abstract class PrewarmerHandoffTestBase(IReleaseSpec spec)
{
    protected IReleaseSpec Spec { get; } = spec;

    // PUSH0 SLOAD PUSH1 1 ADD PUSH0 SSTORE STOP
    private static readonly byte[] CounterCode = [0x5F, 0x54, 0x60, 0x01, 0x01, 0x5F, 0x55, 0x00];
    // PUSH0 PUSH0 REVERT
    private static readonly byte[] RevertCode = [0x5F, 0x5F, 0xFD];
    // MSTORE(0, 0x4e4d); LOG1(0, 32, 1); STOP
    private static readonly byte[] LogCode = [0x61, 0x4E, 0x4D, 0x5F, 0x52, 0x60, 0x01, 0x60, 0x20, 0x5F, 0xA1, 0x00];
    // Deploys the runtime code 0xFE.
    protected static readonly byte[] DeployCode = [0x60, 0xFE, 0x5F, 0x53, 0x60, 0x01, 0x5F, 0xF3];
    // CALL(GAS, CALLER, 0x4e4d, 0, 0, 0, 0); POP; STOP
    private static readonly byte[] PayerCode = [0x5F, 0x5F, 0x5F, 0x5F, 0x61, 0x4E, 0x4D, 0x33, 0x5A, 0xF1, 0x50, 0x00];
    // Without call data SELFDESTRUCT(CALLER), with it SSTORE(0x4e4d, SLOAD(0x4e65746865726d696e64)).
    private static readonly byte[] ChildCode =
        [0x36, 0x60, 0x06, 0x57, 0x33, 0xFF, 0x5B, 0x69, 0x4E, 0x65, 0x74, 0x68, 0x65, 0x72, 0x6D, 0x69, 0x6E, 0x64, 0x54, 0x61, 0x4E, 0x4D, 0x55, 0x00];
    // SSTORE(0, 0x4e4d); SSTORE(1, 2); returns the child code.
    private static readonly byte[] ChildInitCode =
        [0x61, 0x4E, 0x4D, 0x5F, 0x55, 0x60, 0x02, 0x60, 0x01, 0x55, 0x60, 0x18, 0x60, 0x14, 0x5F, 0x39, 0x60, 0x18, 0x5F, 0xF3, .. ChildCode];
    // CREATE2 of the child init code appended to it; with call data, then calls the child.
    private static readonly byte[] FactoryCode =
        [0x60, 0x2C, 0x60, 0x1D, 0x5F, 0x39, 0x61, 0x4E, 0x4D, 0x60, 0x2C, 0x5F, 0x5F, 0xF5, 0x36, 0x60, 0x13, 0x57, 0x00,
         0x5B, 0x5F, 0x5F, 0x5F, 0x5F, 0x5F, 0x85, 0x5A, 0xF1, 0x00, .. ChildInitCode];
    // TSTORE(0, TLOAD(0) + 0x4e4d); SSTORE(CALLER, TLOAD(0) + 0x4e4d); STOP
    private static readonly byte[] TransientCode = [0x5F, 0x5C, 0x61, 0x4E, 0x4D, 0x01, 0x80, 0x5F, 0x5D, 0x33, 0x55, 0x00];
    // SSTORE(0, 0x4e4d); REVERT(0, 0)
    private static readonly byte[] UndoneCode = [0x61, 0x4E, 0x4D, 0x5F, 0x55, 0x5F, 0x5F, 0xFD];
    // Without call data SSTORE(0, BALANCE(C)) if the balance is even, with it SSTORE(1, SLOAD(0)).
    private static readonly byte[] CopierCode =
        [0x36, 0x60, 0x26, 0x57, 0x73, .. TestItem.AddressC.Bytes, 0x31, 0x80, 0x60, 0x01, 0x16, 0x60, 0x24, 0x57, 0x5F, 0x55, 0x00,
         0x5B, 0x00, 0x5B, 0x5F, 0x54, 0x60, 0x01, 0x55, 0x00];

    private static readonly byte[] Salt = [.. new byte[30], 0x4E, 0x4D];
    private static readonly UInt256 ChildSlot = new(0x746865726d696e64UL, 0x4e65UL, 0, 0);

    protected static readonly Address Counter = new("0x000000000000000000004e65746865726d696e64");
    protected static readonly Address Reverter = new("0x00000000000000000000000000000000004e4d01");
    protected static readonly Address Logger = new("0x00000000000000000000000000000000004e4d02");
    protected static readonly Address BalanceReader = new("0x00000000000000000000000000000000004e4d03");
    protected static readonly Address Payer = new("0x00000000000000000000000000000000004e4d04");
    protected static readonly Address Factory = new("0x00000000000000000000000000000000004e4d05");
    protected static readonly Address FreshFactory = new("0x00000000000000000000000000000000004e4d06");
    protected static readonly Address Transient = new("0x00000000000000000000000000000000004e4d07");
    protected static readonly Address Undone = new("0x00000000000000000000000000000000004e4d08");
    protected static readonly Address Caller = new("0x00000000000000000000000000000000004e4d09");
    protected static readonly Address Gift = new("0x00000000000000000000000000000000004e4d0a");
    protected static readonly Address ScarcePayer = new("0x00000000000000000000000000000000004e4d0b");
    protected static readonly Address Fresh = new("0x00000000000000000000000000000000004e4d0c");
    protected static readonly Address Copier = new("0x00000000000000000000000000000000004e4d0d");
    protected static readonly Address Child = ContractAddress.From(Factory, Salt, ChildInitCode);
    protected static readonly Address Ripemd = new("0x0000000000000000000000000000000000000003");
    protected static readonly PrivateKey CodeOwner = TestItem.PrivateKeys[0x4c];

    private IContainer _container = null!;
    private readonly List<BlockHeader> _headers = [];

    protected ILifetimeScope ProcessingScope { get; private set; } = null!;
    protected BlockHeader Parent { get; private set; } = null!;

    protected BlockCachePreWarmer PreWarmer => (BlockCachePreWarmer)ProcessingScope.Resolve<IBlockCachePreWarmer>();
    protected PrewarmerTxAdapter ProductionAdapter => (PrewarmerTxAdapter)ProcessingScope.Resolve<ITransactionProcessorAdapter>();

    [SetUp]
    public void Setup() => Initialize(handoff: true);

    protected void Initialize(bool handoff)
    {
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new BlocksConfig
            {
                PreWarming = PreWarmMode.Block,
                PreWarmStateConcurrency = 3,
                ProcessingCores = ProcessingCores.All,
                PreWarmHandoff = handoff
            }))
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Spec))
            .AddSingleton<IStateHeaderProvider>(new Parents(_headers))
            .Build();

        IMainProcessingModule[] mainModules = _container.Resolve<IMainProcessingModule[]>();
        IWorldStateManager worldStateManager = _container.Resolve<IWorldStateManager>();
        IWorldStateScopeProvider scopeProvider = worldStateManager.GlobalWorldState;
        ProcessingScope = _container.BeginLifetimeScope(b =>
        {
            b.RegisterInstance(scopeProvider).As<IWorldStateScopeProvider>().ExternallyOwned();
            b.RegisterInstance(worldStateManager).As<IWorldStateManager>().ExternallyOwned();
            b.AddModule(mainModules);
        });

        IWorldState worldState = ProcessingScope.Resolve<IWorldState>();
        Hash256 genesisRoot;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(TestItem.AddressA, 1_000.Ether);
            worldState.CreateAccount(TestItem.AddressB, 1_000.Ether);
            worldState.CreateAccount(TestItem.AddressC, 1_000.Ether);
            worldState.CreateAccount(TestItem.AddressD, 1_000.Ether);
            Deploy(worldState, CodeOwner.Address, RevertCode, 1_000.Ether);
            Deploy(worldState, Counter, CounterCode, 0);
            Deploy(worldState, Reverter, RevertCode, 0);
            Deploy(worldState, Logger, LogCode, 0);
            Deploy(worldState, Payer, PayerCode, 1.Ether);
            // PUSH20 C BALANCE PUSH0 SSTORE STOP
            Deploy(worldState, BalanceReader, [0x73, .. TestItem.AddressC.Bytes, 0x31, 0x5F, 0x55, 0x00], 0);
            Deploy(worldState, Factory, FactoryCode, 0);
            Deploy(worldState, FreshFactory, FactoryCode, 0);
            Deploy(worldState, Transient, TransientCode, 0);
            Deploy(worldState, Undone, UndoneCode, 0);
            // CALL(GAS, Undone, 0, 0, 0, 0, 0); POP; STOP
            Deploy(worldState, Caller, [0x5F, 0x5F, 0x5F, 0x5F, 0x5F, 0x73, .. Undone.Bytes, 0x5A, 0xF1, 0x50, 0x00], 0);
            // CALL(GAS, Fresh, 0x4e4d, 0, 0, 0, 0); POP; STOP
            Deploy(worldState, Gift, [0x5F, 0x5F, 0x5F, 0x5F, 0x61, 0x4E, 0x4D, 0x73, .. Fresh.Bytes, 0x5A, 0xF1, 0x50, 0x00], 1.Ether);
            Deploy(worldState, ScarcePayer, PayerCode, 0x4e4d);
            Deploy(worldState, Copier, CopierCode, 0);
            Deploy(worldState, Child, ChildCode, 0x4e4d);
            worldState.Set(new StorageCell(Child, 0), 0x4e4d);
            worldState.Set(new StorageCell(Child, 1), 2);
            worldState.Set(new StorageCell(Child, ChildSlot), 0x4e4d);
            worldState.Commit(Spec);
            worldState.CommitTree(0);
            genesisRoot = worldState.StateRoot;
        }

        Parent = Build.A.BlockHeader
            .WithNumber(0)
            .WithStateRoot(genesisRoot)
            .WithGasLimit(30_000_000)
            .WithHash(Build.A.BlockHeader.TestObject.ParentHash!)
            .TestObject;
        _headers.Clear();
        _headers.Add(Parent);

        void Deploy(IWorldState state, Address address, byte[] code, UInt256 balance)
        {
            state.CreateAccount(address, balance);
            state.InsertCode(address, Keccak.Compute(code), code, Spec);
        }
    }

    [TearDown]
    public void TearDown()
    {
        ProcessingScope?.Dispose();
        _container?.Dispose();
    }

    protected sealed record Run(Hash256 StateRoot, TxReceipt[] Receipts, TransactionResult[] Results, (int Replayed, int Rejected, int Missing) Tally);

    /// <summary>Processes the block by execution, then warmed and handed off, and requires the same results, root and receipts.</summary>
    protected Run Handoff(Block block, IBlockTracer? otherTracer = null) => Handoff(block, PreWarmer, ProductionAdapter, otherTracer);

    protected Run Handoff(Block block, BlockCachePreWarmer preWarmer, PrewarmerTxAdapter adapter, IBlockTracer? otherTracer = null)
    {
        Run executed = Process(block, adapter: null);
        RunPreWarmCaches(preWarmer, block);
        Run run = Process(block, adapter, otherTracer: otherTracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Results, Is.EqualTo(executed.Results));
            Assert.That(run.StateRoot, Is.EqualTo(executed.StateRoot));
        }

        AssertSameReceipts(run.Receipts, executed.Receipts);
        return run;
    }

    // Sync on purpose: the scope is closed on the thread that opened it.
    protected void RunPreWarmCaches(BlockCachePreWarmer preWarmer, Block block)
    {
        IWorldState worldState = ProcessingScope.Resolve<IWorldState>();
        using (worldState.BeginScope(Parent))
        {
            using IDisposable? session = preWarmer.PreWarmCaches(block, Parent, Spec);
            ((PrewarmingSession?)session)?.WaitForCompletion();
        }
    }

    /// <param name="adapter">The adapter that hands off; without one the block is executed.</param>
    protected Run Process(Block block, PrewarmerTxAdapter? adapter, BlockHeader? parent = null, IBlockTracer? otherTracer = null)
    {
        IWorldState worldState = ProcessingScope.Resolve<IWorldState>();
        using (worldState.BeginScope(parent ?? Parent)) return ProcessInScope(block, adapter, otherTracer);
    }

    /// <summary>
    /// Processes the block with the production adapter while its prewarming session is open, ending the session once
    /// the transactions are executed, as block processing does.
    /// </summary>
    /// <param name="ready">Waited for, within a bound, before the transaction at each index.</param>
    private protected Run ProcessWhileWarming(Block block, Func<BlockFootprints, int, bool> ready)
    {
        IWorldState worldState = ProcessingScope.Resolve<IWorldState>();
        using (worldState.BeginScope(Parent))
        {
            using IDisposable? session = PreWarmer.PreWarmCaches(block, Parent, Spec);
            BlockFootprints footprints = PreWarmer.Footprints!;
            return ProcessInScope(block, ProductionAdapter,
                beforeTransaction: index => SpinWait.SpinUntil(() => ready(footprints, index), TimeSpan.FromSeconds(10)),
                transactionsExecuted: () => session?.Dispose());
        }
    }

    private Run ProcessInScope(Block block, PrewarmerTxAdapter? adapter, IBlockTracer? otherTracer = null,
        Action<int>? beforeTransaction = null, Action? transactionsExecuted = null)
    {
        IWorldState worldState = ProcessingScope.Resolve<IWorldState>();
        Block processing = new(block.Header.CloneForProcessing(), block.Body);
        (int Replayed, int Rejected, int Missing) before = adapter?.Tally ?? default;
        ITransactionProcessorAdapter transactions = (ITransactionProcessorAdapter?)adapter ?? new ExecuteTransactionProcessorAdapter(ProcessingScope.Resolve<ITransactionProcessor>());
        BlockReceiptsTracer tracer = new();
        tracer.SetOtherTracer(otherTracer ?? NullBlockTracer.Instance);
        tracer.StartNewBlockTrace(processing);
        transactions.SetBlockExecutionContext(new BlockExecutionContext(processing.Header, Spec));
        List<TransactionResult> results = [];
        for (int i = 0; i < processing.Transactions.Length; i++)
        {
            beforeTransaction?.Invoke(i);
            Transaction tx = processing.Transactions[i];
            using ITxTracer txTracer = tracer.StartNewTxTrace(tx);
            results.Add(transactions.Execute(tx, tracer));
            tracer.EndTxTrace();
        }

        transactionsExecuted?.Invoke();
        worldState.Commit(Spec);
        worldState.CommitTree(block.Number);
        (int Replayed, int Rejected, int Missing) after = adapter?.Tally ?? default;
        return new Run(worldState.StateRoot, [.. tracer.TxReceipts], [.. results],
            (after.Replayed - before.Replayed, after.Rejected - before.Rejected, after.Missing - before.Missing));
    }

    protected static void AssertSameReceipts(TxReceipt[] actual, TxReceipt[] expected)
    {
        Assert.That(actual, Has.Length.EqualTo(expected.Length));
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.That(actual[i].Logs, Has.Length.EqualTo(expected[i].Logs!.Length), $"log count of {i}");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual[i].StatusCode, Is.EqualTo(expected[i].StatusCode), $"status of {i}");
                Assert.That(actual[i].GasUsed, Is.EqualTo(expected[i].GasUsed), $"gas of {i}");
                Assert.That(actual[i].GasUsedTotal, Is.EqualTo(expected[i].GasUsedTotal), $"cumulative gas of {i}");
                Assert.That(actual[i].ContractAddress, Is.EqualTo(expected[i].ContractAddress), $"contract address of {i}");
                Assert.That(actual[i].Recipient, Is.EqualTo(expected[i].Recipient), $"recipient of {i}");
                for (int j = 0; j < expected[i].Logs!.Length; j++)
                {
                    Assert.That(actual[i].Logs![j].Address, Is.EqualTo(expected[i].Logs![j].Address), $"log {j} address of {i}");
                    Assert.That(actual[i].Logs![j].Data, Is.EqualTo(expected[i].Logs![j].Data), $"log {j} data of {i}");
                    Assert.That(actual[i].Logs![j].Topics, Is.EqualTo(expected[i].Logs![j].Topics), $"log {j} topics of {i}");
                }
            }
        }
    }

    protected Block BuildBlock(params Transaction[] transactions) => BuildBlock(Parent, transactions);

    protected Block BuildBlock(BlockHeader parent, params Transaction[] transactions) => BuildBlock(parent, 30_000_000, transactions);

    protected Block BuildBlock(BlockHeader parent, ulong gasLimit, params Transaction[] transactions) =>
        Build.A.Block.WithNumber(parent.Number + 1)
            .WithParent(parent)
            .WithBeneficiary(TestItem.AddressF)
            .WithBaseFeePerGas(1.GWei)
            .WithTimestamp(parent.Timestamp + 12)
            .WithTransactions(transactions)
            .WithGasLimit(gasLimit)
            .WithExcessBlobGas(Spec.IsEip4844Enabled ? 0 : null)
            .WithBlobGasUsed(Spec.IsEip4844Enabled ? 0 : null)
            .TestObject;

    /// <summary>The header of <paramref name="block"/> processed into <paramref name="stateRoot"/>.</summary>
    protected BlockHeader Processed(Block block, Hash256 stateRoot)
    {
        BlockHeader header = block.Header.Clone();
        header.StateRoot = stateRoot;
        header.Hash = header.CalculateHash();
        _headers.Add(header);
        return header;
    }

    protected static Transaction Call(PrivateKey sender, ulong nonce, Address to, ulong gasLimit = 100_000, byte[]? data = null) =>
        Build.A.Transaction.WithType(TxType.EIP1559).WithNonce(nonce).WithTo(to).WithValue(UInt256.Zero).WithGasLimit(gasLimit).WithData(data ?? [])
            .WithMaxFeePerGas(2.GWei).WithMaxPriorityFeePerGas(1.GWei)
            .SignedAndResolved(sender).TestObject;

    protected static Transaction Transfer(PrivateKey sender, ulong nonce, Address to, UInt256 value, ulong gasLimit = GasCostOf.Transaction) =>
        Build.A.Transaction.WithType(TxType.EIP1559).WithNonce(nonce).WithTo(to).WithValue(value).WithGasLimit(gasLimit)
            .WithMaxFeePerGas(2.GWei).WithMaxPriorityFeePerGas(1.GWei)
            .SignedAndResolved(sender).TestObject;

    protected static Transaction Create(PrivateKey sender, ulong nonce, byte[] initCode) =>
        Build.A.Transaction.WithType(TxType.EIP1559).WithNonce(nonce).WithCode(initCode).WithGasLimit(200_000)
            .WithMaxFeePerGas(2.GWei).WithMaxPriorityFeePerGas(1.GWei)
            .SignedAndResolved(sender).TestObject;

    protected static Transaction LegacyCall(PrivateKey sender, ulong nonce, Address to) =>
        Build.A.Transaction.WithType(TxType.Legacy).WithNonce(nonce).WithTo(to).WithGasLimit(100_000).WithGasPrice(2.GWei)
            .SignedAndResolved(sender).TestObject;

    protected static Transaction AccessListCall(PrivateKey sender, ulong nonce, Address to) =>
        Build.A.Transaction.WithType(TxType.AccessList).WithNonce(nonce).WithTo(to).WithGasLimit(100_000).WithGasPrice(2.GWei)
            .WithAccessList(new AccessList.Builder().AddAddress(to).AddStorage(0).Build())
            .SignedAndResolved(sender).TestObject;

    protected static Transaction BlobCall(PrivateKey sender, ulong nonce, Address to) =>
        Build.A.Transaction.WithShardBlobTxTypeAndFields(1, isMempoolTx: false).WithMaxFeePerBlobGas(0x4e4d)
            .WithNonce(nonce).WithTo(to).WithGasLimit(100_000).WithMaxFeePerGas(2.GWei).WithMaxPriorityFeePerGas(1.GWei)
            .SignedAndResolved(sender).TestObject;

    /// <summary>A set-code transaction delegating <paramref name="authority"/> to <paramref name="code"/> and calling it.</summary>
    protected static Transaction SetCodeCall(PrivateKey sender, ulong nonce, PrivateKey authority, Address code) =>
        Build.A.Transaction.WithType(TxType.SetCode).WithNonce(nonce).WithTo(authority.Address).WithGasLimit(150_000)
            .WithMaxFeePerGas(2.GWei).WithMaxPriorityFeePerGas(1.GWei)
            .WithAuthorizationCode(new EthereumEcdsa(0).Sign(authority, 0, code, 0))
            .SignedAndResolved(sender).TestObject;

    private sealed class Parents(List<BlockHeader> headers) : IStateHeaderProvider
    {
        public BlockHeader? FindParentHeader(BlockHeader target) => headers.Find(header => header.Hash == target.ParentHash);
        public ulong FinalizedBlockNumber => 0;
        public BlockHeader? GetFinalizedHeader(ulong blockNumber) => null;
    }
}
