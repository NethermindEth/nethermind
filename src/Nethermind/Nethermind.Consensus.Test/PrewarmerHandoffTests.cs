// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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
using Nethermind.Core.Test.Blockchain;
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
using Nethermind.Serialization.Rlp;
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

        using (Assert.EnterMultipleScope())
        {
            // The later deployments read the factory's nonce, which the first one increments.
            Assert.That(replayed, Is.EqualTo(1));
            // Their creations clear the storage the first one wrote, so their runs read it as cleared whatever the seed.
            Assert.That(PreWarmer.Footprints!.RefreshesRun, Is.LessThanOrEqualTo(4));
        }
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
    public void Decorating_block_processing_components_leaves_the_handoff_on([Values] bool worldState)
    {
        Action<ContainerBuilder> decorate = worldState
            ? static builder => builder.AddDecorator<IWorldState, ForwardingWorldState>()
            : static builder => builder.AddDecorator<ITransactionProcessor, ForwardingProcessor>();
        TearDown();
        Initialize(handoff: true, decorate);

        Assert.That(Handoff(ThreeIndependentTransactions()).Tally.Replayed, Is.EqualTo(3));
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
    public void Envs_record_only_under_the_marked_transaction_processor_registration([Values] bool replaced)
    {
        TearDown();
        Initialize(handoff: true, replaced ? static builder => builder.AddSingleton(Substitute.For<ITransactionProcessor>()) : null);

        Assert.That(ProcessingScope.Resolve<PrewarmerEnvFactory>().RecordsFootprints, Is.EqualTo(!replaced));
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
            footprints.QueueExecuted(0, writes);
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
    public void A_refreshed_run_that_reads_a_seeded_slot_at_another_value_is_taken_again_only_after_a_write_changes([Values] bool writeChanges)
    {
        (BlockFootprints footprints, Transaction[] txs) = Footprints(2);
        StorageCell cell = new(TestItem.AddressC, 0x4e4d);
        footprints.Store(0, Footprint(txs[0], writes: [(cell, 0x4e4d)]));
        footprints.Store(1, Footprint(txs[1], reads: [(cell, UInt256.Zero)]));
        Assert.That(footprints.TryTakeInvalidated(-1, out _), Is.True);

        int seededAt = footprints.WritesVersion;
        if (writeChanges) footprints.Store(0, Footprint(txs[0], writes: [(cell, 2 * 0x4e4d)]));
        footprints.Store(1, Footprint(txs[1], reads: [(cell, UInt256.Zero)]), seededAt);

        Assert.That(footprints.TryTakeInvalidated(-1, out _), Is.EqualTo(writeChanges));
    }

    [Test]
    public void A_reader_past_a_slots_indexed_readers_is_still_checked_against_the_writes_before_it()
    {
        int readers = BlockFootprints.MaxIndexedPerSlot + 1;
        (BlockFootprints footprints, Transaction[] txs) = Footprints(readers + 1);
        StorageCell cell = new(TestItem.AddressC, 0x4e4d);
        footprints.Store(0, Footprint(txs[0], writes: [(cell, 0x4e4d)]));
        for (int i = 1; i < readers; i++) footprints.Store(i, Footprint(txs[i], reads: [(cell, 0x4e4d)]));
        footprints.Store(readers, Footprint(txs[readers], reads: [(cell, UInt256.Zero)]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(footprints.TryTakeInvalidated(-1, out int position), Is.True);
            Assert.That(position, Is.EqualTo(readers));
        }
    }

    [Test]
    public void A_refresh_that_fails_leaves_the_block_its_other_refreshes()
    {
        PreBlockCaches caches = ProcessingScope.Resolve<PreBlockCaches>();
        StrongBox<int> seeds = new();
        using BlockCachePreWarmer preWarmer = new(
            new OuterDecoratedEnvs(ProcessingScope.Resolve<PrewarmerEnvFactory>(), caches, state => new FirstSeedFails(state, seeds)),
            minPoolSize: 4,
            concurrency: 3,
            parallelExecutionBatchRead: true,
            ProcessingScope.Resolve<NodeStorageCache>(),
            caches,
            LimboLogs.Instance,
            handoff: true);

        RunPreWarmCaches(preWarmer, BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Counter),
            Call(TestItem.PrivateKeyB, 0, Counter),
            Call(TestItem.PrivateKeyD, 0, Counter)));

        BlockFootprints footprints = preWarmer.Footprints!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(seeds.Value, Is.GreaterThan(1), "a refresh seeded after the one that failed");
            Assert.That(footprints.Get(1)!.Refreshed || footprints.Get(2)!.Refreshed, Is.True);
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
    public void A_transaction_executed_into_the_writes_its_footprint_predicted_does_not_wake_the_refresh_worker()
    {
        // The call reads a balance the transfer changes, so it is executed; its guard slot is set and reset, which the
        // commit does not report and the footprint does not predict.
        Block block = BuildBlock(
            Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressC, 1.Wei),
            Call(TestItem.PrivateKeyB, 0, Guarded),
            Transfer(TestItem.PrivateKeyD, 0, TestItem.AddressA, 1.Wei));
        // The pass is waited on and over before processing, so no refresh worker takes the wakes counted here.
        RunPreWarmCaches(PreWarmer, block);
        BlockFootprints footprints = PreWarmer.Footprints!;
        int wakes = footprints.PendingWakes;

        Run run = Process(block, ProductionAdapter);

        int pending = footprints.PendingWakes;
        footprints.ApplyExecuted();

        using (Assert.EnterMultipleScope())
        {
            Assert.That((run.Tally.Replayed, run.Tally.Rejected), Is.EqualTo((2, 1)));
            Assert.That(pending, Is.EqualTo(wakes));
            // Reported all the same, with only the slot it changed.
            Assert.That(footprints.ValueBefore(new StorageCell(Guarded, 1), 2), Is.EqualTo((UInt256)1));
            Assert.That(footprints.ValueBefore(new StorageCell(Guarded, 0), 2), Is.Null);
        }
    }

    [Test]
    public void The_writes_a_transaction_reported_stay_as_they_were_while_later_transactions_report_theirs()
    {
        // The refresh worker reads a report after block processing has moved on to collect the next ones; counts of 0 to 6
        // writes put a transaction across the first chunk's end.
        CommittedStorageWrites committed = new();
        List<(ReadOnlyMemory<(StorageCell Cell, UInt256 Value)> Writes, int Transaction)> reports = [];
        // The last transaction writes more slots than a chunk holds.
        static int Writes(int tx) => tx == 199 ? 600 : tx % 7;
        for (int tx = 0; tx < 200; tx++)
        {
            committed.Begin();
            for (int i = 0; i < Writes(tx); i++) committed.Add(TestItem.AddressC, (UInt256)i, (UInt256)tx);
            reports.Add((committed.End(), tx));
        }

        Assert.That(reports, Has.All.Matches<(ReadOnlyMemory<(StorageCell Cell, UInt256 Value)> Writes, int Transaction)>(report =>
            report.Writes.Length == Writes(report.Transaction)
            && Enumerable.Range(0, report.Writes.Length).All(i =>
                report.Writes.Span[i].Equals((new StorageCell(TestItem.AddressC, (UInt256)i), (UInt256)report.Transaction)))));
    }

    [Test]
    public void Reports_read_while_later_transactions_report_theirs_are_as_they_were_reported()
    {
        // As the refresh worker reads them, on another thread, while block processing collects more.
        CommittedStorageWrites committed = new();
        System.Collections.Concurrent.ConcurrentQueue<(ReadOnlyMemory<(StorageCell Cell, UInt256 Value)> Writes, int Transaction)> reports = new();
        const int transactions = 20_000;
        static int Writes(int tx) => tx % 9;
        Task reporting = Task.Run(() =>
        {
            for (int tx = 0; tx < transactions; tx++)
            {
                committed.Begin();
                for (int i = 0; i < Writes(tx); i++) committed.Add(TestItem.AddressC, (UInt256)i, (UInt256)tx);
                reports.Enqueue((committed.End(), tx));
            }
        });

        int read = 0;
        int wrong = 0;
        // Ends once every report is read, or once the reporting has stopped and nothing is left to read.
        while (read < transactions && !(reporting.IsCompleted && reports.IsEmpty))
        {
            if (!reports.TryDequeue(out (ReadOnlyMemory<(StorageCell Cell, UInt256 Value)> Writes, int Transaction) report)) continue;
            read++;
            ReadOnlySpan<(StorageCell Cell, UInt256 Value)> writes = report.Writes.Span;
            if (writes.Length != Writes(report.Transaction)) wrong++;
            for (int i = 0; i < writes.Length; i++)
            {
                if (!writes[i].Equals((new StorageCell(TestItem.AddressC, (UInt256)i), (UInt256)report.Transaction))) wrong++;
            }
        }

        reporting.Wait();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(read, Is.EqualTo(transactions));
            Assert.That(wrong, Is.Zero);
        }
    }

    [Test]
    public void Commits_outside_a_reported_transaction_take_no_room_from_the_reports()
    {
        // Replays and the block's own commits also commit storage; collecting them would allocate chunks for nothing.
        CommittedStorageWrites committed = new();
        committed.Begin();
        committed.Add(TestItem.AddressC, 0, 1);
        MemoryMarshal.TryGetArray(committed.End(), out ArraySegment<(StorageCell Cell, UInt256 Value)> first);
        for (int i = 0; i < 1000; i++) committed.Add(TestItem.AddressC, (UInt256)i, 2);
        committed.Begin();
        committed.Add(TestItem.AddressC, 0, 3);
        MemoryMarshal.TryGetArray(committed.End(), out ArraySegment<(StorageCell Cell, UInt256 Value)> next);

        Assert.That((next.Array, next.Offset), Is.EqualTo((first.Array, 1)));
    }

    [Test]
    public void A_session_whose_refresh_worker_is_never_woken_by_block_processing_still_ends()
    {
        // The executed call's writes are the ones its footprint predicted, so block processing leaves the blocked worker
        // asleep; ending the session must still release it.
        Block block = BuildBlock(
            Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressC, 1.Wei),
            Call(TestItem.PrivateKeyB, 0, Guarded),
            Transfer(TestItem.PrivateKeyD, 0, TestItem.AddressA, 1.Wei));
        Run? run = null;
        Task processing = Task.Run(() => run = ProcessWhileWarming(block, (footprints, index) =>
            index > 0 || (footprints.WarmPassEnded && !footprints.HasWork && footprints.Get(1) is not null)));

        Assert.That(processing.Wait(TimeSpan.FromSeconds(30)), Is.True, "the session did not end");
        Assert.That(run!.Tally.Rejected, Is.EqualTo(1));
    }

    [Test]
    public void A_report_against_a_footprint_too_large_to_compare_wakes_the_refresh_worker()
    {
        // Comparing a write with each of many effects would cost block processing more than the wake it saves.
        (BlockFootprints footprints, Transaction[] txs) = Footprints(2);
        StorageCell cell = new(TestItem.AddressC, 0x4e4d);
        StateEffect[] effects =
        [
            new StateEffect { Kind = EffectKind.SetStorage, Cell = cell, Value = 0x4e4d },
            .. Enumerable.Repeat(new StateEffect { Kind = EffectKind.AddToBalance, Cell = new StorageCell(TestItem.AddressD, default), Value = 1 }, 300)
        ];
        footprints.Store(0, new TransactionFootprint(txs[0], [], [], effects, default, default, default));

        footprints.QueueExecuted(0, new[] { (cell, (UInt256)0x4e4d) });

        Assert.That(footprints.PendingWakes, Is.GreaterThan(0));
    }

    public enum Report { AsPredicted, OtherValue, OtherSlot, Subset, Restored, NoneWithoutFootprint, SomeWithoutFootprint }

    [Test]
    public void Block_processing_wakes_the_refresh_worker_only_for_writes_its_footprints_did_not_predict([Values] Report report)
    {
        (BlockFootprints footprints, Transaction[] txs) = Footprints(2);
        StorageCell cell = new(TestItem.AddressC, 0x4e4d);
        StorageCell other = new(TestItem.AddressC, 2 * 0x4e4d);
        switch (report)
        {
            case Report.AsPredicted or Report.OtherValue or Report.OtherSlot:
                footprints.Store(0, Footprint(txs[0], writes: [(cell, 0x4e4d)]));
                break;
            case Report.Subset:
                footprints.Store(0, Footprint(txs[0], writes: [(cell, 0x4e4d), (other, 0x4e4d)]));
                break;
            case Report.Restored:
                // A slot written back to its value at the transaction's start: neither the footprint nor the commit has it.
                footprints.Store(0, Footprint(txs[0]));
                break;
        }

        (StorageCell Cell, UInt256 Value)[] reported = report switch
        {
            // Committed by the main state, so not the instance the footprint holds.
            Report.AsPredicted or Report.Subset => [(new StorageCell(new Address(cell.Address.Bytes), 0x4e4d), 0x4e4d)],
            Report.OtherSlot => [(other, 0x4e4d)],
            Report.NoneWithoutFootprint or Report.Restored => [],
            _ => [(cell, 2 * 0x4e4d)]
        };
        footprints.QueueExecuted(0, reported);

        // Waking a blocked worker costs block processing microseconds; skipping a wake it needs delays its refreshes.
        bool woken = footprints.PendingWakes > 0;

        // Skipped or not, the report takes the place of what a footprint stored later predicts.
        footprints.ApplyExecuted();
        footprints.Store(0, Footprint(txs[0], writes: [(cell, 3 * 0x4e4d)]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(woken, Is.EqualTo(report is Report.OtherValue or Report.OtherSlot or Report.Subset or Report.SomeWithoutFootprint));
            Assert.That(footprints.ValueBefore(cell, 1), Is.EqualTo(report is Report.OtherSlot || reported.Length == 0 ? null : reported[0].Value));
        }
    }

    [Test]
    public void A_footprint_stored_after_its_transaction_was_executed_leaves_the_executed_writes()
    {
        (BlockFootprints footprints, Transaction[] txs) = Footprints(2);
        StorageCell cell = new(TestItem.AddressC, 0x4e4d);
        footprints.QueueExecuted(0, new[] { (cell, (UInt256)(2 * 0x4e4d)) });
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

        footprints.QueueExecuted(0, new[] { (cell, sameValue ? (UInt256)0x4e4d : 2 * 0x4e4d) });
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

    internal static (BlockFootprints Footprints, Transaction[] Transactions) Footprints(int count)
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

    internal static TransactionFootprint Footprint(Transaction tx, (StorageCell Cell, UInt256 Value)[]? reads = null, (StorageCell Cell, UInt256 Value)[]? writes = null) =>
        new(tx, [],
            [.. (reads ?? []).Select(static read => new SlotPrecondition { Cell = read.Cell, Value = read.Value, Read = true })],
            [.. (writes ?? []).Select(static write => new StateEffect { Kind = EffectKind.SetStorage, Cell = write.Cell, Value = write.Value })],
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

    public enum RecordedChanges { NetCredit, NetDebit, NetZeroOfEmpty, AcrossRecreation, SlotWrittenTwice, SlotRestored, SlotRestoredUnread, SlotRestoredAfterClear, SlotChangedAfterRestore, ZeroValueToCode, ZeroValueToEmpty, ZeroValueToRecreated }

    [Test]
    public void A_compacted_footprint_replays_into_the_committed_state_of_its_run([Values] RecordedChanges changes)
    {
        StorageCell cell = new(Counter, 0x4e4d);
        // Starts at 0x4e4d.
        StorageCell set = new(Child, 0);
        Address empty = TestItem.AddressE;
        BlockHeader parent = WithEmptyAccount(empty);
        IWorldState worldState = ProcessingScope.Resolve<IWorldState>();
        (UInt256, UInt256, bool, UInt256, UInt256, bool, bool) State() =>
            (worldState.GetBalance(ScarcePayer), worldState.GetBalance(TestItem.AddressC), worldState.AccountExists(TestItem.AddressC), Get(worldState, cell),
                Get(worldState, set), worldState.AccountExists(empty), worldState.AccountExists(Logger));

        TransactionFootprint footprint;
        int compacted;
        (UInt256, UInt256, bool, UInt256, UInt256, bool, bool) recorded;
        using (worldState.BeginScope(parent))
        {
            (footprint, _) = Record(worldState, recorder =>
            {
                switch (changes)
                {
                    case RecordedChanges.NetCredit:
                        recorder.SubtractFromBalance(ScarcePayer, 0x4e4d, Spec, out _);
                        recorder.AddToBalance(ScarcePayer, 0x4e4c, Spec, out _);
                        recorder.AddToBalance(ScarcePayer, 3, Spec, out _);
                        return 1;
                    case RecordedChanges.NetDebit:
                        recorder.AddToBalance(ScarcePayer, 1, Spec, out _);
                        recorder.SubtractFromBalance(ScarcePayer, 0x4e4e, Spec, out _);
                        return 1;
                    case RecordedChanges.NetZeroOfEmpty:
                        // Updated back to empty, the account is deleted (EIP-161); no change at all leaves it.
                        recorder.AddToBalance(empty, 3, Spec, out _);
                        recorder.SubtractFromBalance(empty, 3, Spec, out _);
                        return 2;
                    case RecordedChanges.AcrossRecreation:
                        recorder.AddToBalance(TestItem.AddressC, 3, Spec, out _);
                        recorder.DeleteAccount(TestItem.AddressC);
                        recorder.CreateAccount(TestItem.AddressC, 0);
                        recorder.AddToBalance(TestItem.AddressC, 2, Spec, out _);
                        return 4;
                    case RecordedChanges.SlotWrittenTwice:
                        recorder.Set(cell, 1);
                        recorder.Set(cell, 2);
                        return 1;
                    case RecordedChanges.SlotRestored:
                        // Replay requires the value read, so writing it back changes nothing.
                        recorder.Get(set, out _);
                        recorder.Set(set, 5);
                        recorder.Set(set, 0x4e4d);
                        return 0;
                    case RecordedChanges.SlotRestoredUnread:
                        recorder.Set(set, 5);
                        recorder.Set(set, 0x4e4d);
                        return 1;
                    case RecordedChanges.SlotRestoredAfterClear:
                        recorder.Get(set, out _);
                        recorder.ClearStorage(Child);
                        recorder.Set(set, 0x4e4d);
                        return 2;
                    case RecordedChanges.SlotChangedAfterRestore:
                        recorder.Set(set, 0x4e4d);
                        recorder.Set(set, 5);
                        return 1;
                    case RecordedChanges.ZeroValueToCode:
                        recorder.GetCode(Counter);
                        recorder.AddToBalance(Counter, 0, Spec, out _);
                        recorder.SubtractFromBalance(ScarcePayer, 0, Spec, out _);
                        return 0;
                    case RecordedChanges.ZeroValueToEmpty:
                        // The touch of an empty account deletes it (EIP-161).
                        recorder.GetCode(empty);
                        recorder.AddToBalance(empty, 0, Spec, out _);
                        return 1;
                    default:
                        // Recreated empty, the contract is deleted only by the touch.
                        recorder.GetCode(Logger);
                        recorder.DeleteAccount(Logger);
                        recorder.CreateAccount(Logger, 0);
                        recorder.AddToBalance(Logger, 0, Spec, out _);
                        return 3;
                }
            }, out compacted);
            worldState.Commit(Spec);
            recorded = State();
        }

        using (worldState.BeginScope(parent))
        {
            footprint.Replay(worldState, Spec);
            worldState.Commit(Spec);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(State(), Is.EqualTo(recorded));
                Assert.That(footprint.Effects.Length, Is.EqualTo(compacted));
            }
        }

        static UInt256 Get(IWorldState state, in StorageCell cell)
        {
            state.Get(in cell, out UInt256 value);
            return value;
        }
    }

    [Test]
    public void A_zero_value_credit_to_an_account_whose_code_the_run_did_not_require_still_needs_the_account()
    {
        IWorldState worldState = ProcessingScope.Resolve<IWorldState>();
        TransactionFootprint footprint;
        using (worldState.BeginScope(Parent))
        {
            (footprint, _) = Record(worldState, recorder =>
            {
                recorder.AddToBalance(Logger, 0, Spec, out _);
                return 1;
            }, out _);
        }

        // An earlier transaction of the block destroyed the contract; the touch the run made cannot be made.
        using (worldState.BeginScope(Parent))
        {
            worldState.DeleteAccount(Logger);
            Assert.That(() => footprint.Replay(worldState, Spec), Throws.InvalidOperationException);
        }
    }

    /// <summary>A child of <see cref="Parent"/> whose state holds <paramref name="address"/> as an empty account.</summary>
    private BlockHeader WithEmptyAccount(Address address)
    {
        IWorldState worldState = ProcessingScope.Resolve<IWorldState>();
        using (worldState.BeginScope(Parent))
        {
            worldState.CreateAccount(address, 0);
            // As genesis, which keeps empty accounts.
            worldState.Commit(Spec, Evm.Tracing.State.NullStateTracer.Instance, isGenesis: true);
            worldState.CommitTree(Parent.Number + 1);
            return Processed(BuildBlock(), worldState.StateRoot);
        }
    }

    [Test]
    public void A_net_balance_change_is_not_replayed_where_a_change_it_merges_could_not_be_made([Values] bool minimumNotedFirst)
    {
        IWorldState worldState = ProcessingScope.Resolve<IWorldState>();
        using (worldState.BeginScope(Parent))
        {
            // Nets to two wei paid, from a balance that must hold all of it first; or a smaller deficit after a check of
            // the whole balance, which still stands.
            (TransactionFootprint footprint, Snapshot before) = Record(worldState, recorder =>
            {
                if (minimumNotedFirst)
                {
                    recorder.NoteMinimumBalance(ScarcePayer, 0x4e4d);
                    recorder.SubtractFromBalance(ScarcePayer, 2, Spec, out _);
                    recorder.AddToBalance(ScarcePayer, 1, Spec, out _);
                    return 2;
                }

                recorder.SubtractFromBalance(ScarcePayer, 0x4e4d, Spec, out _);
                recorder.AddToBalance(ScarcePayer, 0x4e4c, Spec, out _);
                recorder.SubtractFromBalance(ScarcePayer, 1, Spec, out _);
                return 3;
            }, out _);
            worldState.Restore(before);
            bool met = footprint.Matches(worldState);
            worldState.SubtractFromBalance(ScarcePayer, 1, Spec, out _);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(met, Is.True);
                Assert.That(footprint.Matches(worldState), Is.False);
            }
        }
    }

    [Test]
    public void A_run_reading_more_accounts_and_slots_than_the_buffers_hold_is_recorded()
    {
        IWorldState worldState = ProcessingScope.Resolve<IWorldState>();
        using (worldState.BeginScope(Parent))
        {
            FootprintRecorder recorder = new(worldState);
            // The first run grows the buffers past what is retained, so the second grows them again from the start.
            foreach ((int accounts, int slots) in new[] { (2, 1100), (40, 70) })
            {
                Transaction tx = Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressB, 1.Wei);
                recorder.Start(new Progress { MainThreadTxIndex = -1 }, txIndex: 0, CancellationToken.None);
                recorder.GetNonce(TestItem.AddressA);
                for (int i = 0; i < accounts; i++) recorder.GetBalance(TestItem.Addresses[i]);
                for (int i = 0; i < slots; i++) recorder.Get(new StorageCell(Counter, (UInt256)i), out _);
                recorder.Outcome.MarkAsSuccess(TestItem.AddressB, default, [], []);
                TransactionFootprint? footprint = recorder.Finish(tx, TransactionResult.Ok);
                recorder.Stop();

                Snapshot recorded = worldState.TakeSnapshot();
                bool met = footprint?.Matches(worldState) == true;
                // The last account read is past the account buffer's first size.
                worldState.AddToBalanceAndCreateIfNotExists(TestItem.Addresses[accounts - 1], 1, Spec, out _);
                bool metAfterChange = footprint?.Matches(worldState) == true;
                worldState.Restore(recorded);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(footprint?.Slots.Length, Is.EqualTo(slots));
                    Assert.That(met, Is.True);
                    Assert.That(metAfterChange, Is.False);
                }
            }
        }
    }

    [Test]
    public void A_run_recorded_without_its_machine_counts_as_reading_the_whole_block_context()
    {
        IWorldState worldState = ProcessingScope.Resolve<IWorldState>();
        using (worldState.BeginScope(Parent))
        {
            (TransactionFootprint footprint, _) = Record(worldState, static _ => 0, out _);

            Assert.That(footprint.ContextReads,
                Is.EqualTo(BlockContextReads.Coinbase | BlockContextReads.Timestamp | BlockContextReads.GasLimit | BlockContextReads.PrevRandao
                    | BlockContextReads.OutOfGas));
        }
    }

    /// <summary>Records <paramref name="run"/> as the footprint of a transfer from A, returning the state before it.</summary>
    private (TransactionFootprint Footprint, Snapshot Before) Record(IWorldState worldState, Func<FootprintRecorder, int> run, out int changes)
    {
        Transaction tx = Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressB, 1.Wei);
        Snapshot before = worldState.TakeSnapshot();
        FootprintRecorder recorder = new(worldState);
        recorder.Start(new Progress { MainThreadTxIndex = -1 }, txIndex: 0, CancellationToken.None);
        recorder.GetNonce(TestItem.AddressA);
        changes = run(recorder);
        recorder.Outcome.MarkAsSuccess(TestItem.AddressB, default, [], []);
        TransactionFootprint? footprint = recorder.Finish(tx, TransactionResult.Ok);
        recorder.Stop();
        Assert.That(footprint, Is.Not.Null);
        return (footprint!, before);
    }

    private Block ThreeIndependentTransactions() => BuildBlock(
        Call(TestItem.PrivateKeyA, 0, Counter),
        Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 1.Wei),
        Call(TestItem.PrivateKeyD, 0, Logger));

    private sealed class Progress : IBlockProcessingProgress
    {
        public int MainThreadTxIndex { get; set; }
    }

    /// <summary>
    /// The prewarmer wraps its world state in a <see cref="FootprintRecorder"/> when handoff is on, the default; the
    /// address warmer prefetches only when that wrapper reports and forwards the backend's prefetch support.
    /// </summary>
    [Test]
    public void FootprintRecorder_forwards_prefetch_to_the_recorded_state([Values] bool supported)
    {
        PrefetchRecordingState inner = new(Substitute.For<IWorldState>(), supported);
        IWorldState recorder = new FootprintRecorder(inner);
        Address[] accounts = [TestItem.AddressA, TestItem.AddressB];
        StorageCell[] slots = [new(TestItem.AddressA, 1)];

        recorder.Prefetch(accounts, slots, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorder.SupportsPrefetch, Is.EqualTo(supported));
            Assert.That(inner.Accounts, Is.EqualTo(accounts));
            Assert.That(inner.Slots, Is.EqualTo(slots));
        }
    }

    private sealed class PrefetchRecordingState(IWorldState state, bool supported) : WorldStateDecorator(state), IWorldState
    {
        public List<Address> Accounts { get; } = [];
        public List<StorageCell> Slots { get; } = [];

        bool IWorldState.SupportsPrefetch => supported;

        void IWorldState.Prefetch(Span<Address> accounts, Span<StorageCell> slots, CancellationToken cancellationToken)
        {
            Accounts.AddRange(accounts);
            Slots.AddRange(slots);
        }
    }

    private sealed class ForwardingWorldState(IWorldState state) : WorldStateDecorator(state);

    private sealed class ForwardingProcessor(ITransactionProcessor processor) : ITransactionProcessor
    {
        public TransactionResult Process(Transaction transaction, ITxTracer txTracer, ExecutionOptions options) =>
            processor.Process(transaction, txTracer, options);

        public void SetBlockExecutionContext(BlockHeader blockHeader) => processor.SetBlockExecutionContext(blockHeader);

        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) =>
            processor.SetBlockExecutionContext(in blockExecutionContext);
    }

    /// <summary>Envs whose scopes expose their world state through one more decorator than the recorder.</summary>
    private sealed class OuterDecoratedEnvs(PrewarmerEnvFactory factory, PreBlockCaches caches, Func<IWorldState, IWorldState>? decorate = null)
        : IPooledObjectPolicy<IPrewarmerEnv>
    {
        public IPrewarmerEnv Create() => new Env(factory.Create(caches), decorate ?? (static state => new Forwarding(state)));

        public bool Return(IPrewarmerEnv obj) => true;

        private sealed class Env(IPrewarmerEnv inner, Func<IWorldState, IWorldState> decorate) : IPrewarmerEnv
        {
            public ReadOnlySpan<IHasAccessList> SystemAccessLists => inner.SystemAccessLists;

            FootprintRecorder? IPrewarmerEnv.Recorder => inner.Recorder;

            public bool TryBuild(BlockHeader? baseBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope)
            {
                scope = inner.TryBuild(baseBlock, out IReadOnlyTxProcessingScope? built) ? new Scope(built, decorate) : null;
                return scope is not null;
            }

            public bool TryBuildAtTarget(BlockHeader targetBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope)
            {
                scope = inner.TryBuildAtTarget(targetBlock, out IReadOnlyTxProcessingScope? built) ? new Scope(built, decorate) : null;
                return scope is not null;
            }

            public void Dispose() => inner.Dispose();
        }

        private sealed class Scope(IReadOnlyTxProcessingScope inner, Func<IWorldState, IWorldState> decorate) : IReadOnlyTxProcessingScope
        {
            public ITransactionProcessor TransactionProcessor => inner.TransactionProcessor;
            public IWorldState WorldState { get; } = decorate(inner.WorldState);
            public void Reset() => inner.Reset();
            public void Dispose() => inner.Dispose();
        }

        private sealed class Forwarding(IWorldState state) : WorldStateDecorator(state);
    }

    /// <summary>A scope's world state that fails the first slot a refresh seeds: only a refresh sets one through it.</summary>
    private sealed class FirstSeedFails(IWorldState state, StrongBox<int> seeds) : WorldStateDecorator(state)
    {
        public override void Set(in StorageCell storageCell, in UInt256 newValue)
        {
            if (Interlocked.Increment(ref seeds.Value) == 1) throw new InvalidOperationException("The first seed fails.");
            base.Set(in storageCell, in newValue);
        }
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
    protected static readonly Address Guarded = new("0x00000000000000000000000000000000004e4d0e");
    // Each stores the block context field it reads: COINBASE, TIMESTAMP, GASLIMIT or PREVRANDAO, then PUSH0 SSTORE STOP.
    protected static readonly Address CoinbaseReader = new("0x00000000000000000000000000000000004e4d10");
    protected static readonly Address TimestampReader = new("0x00000000000000000000000000000000004e4d11");
    protected static readonly Address GasLimitReader = new("0x00000000000000000000000000000000004e4d12");
    protected static readonly Address PrevRandaoReader = new("0x00000000000000000000000000000000004e4d13");
    // PUSH20 F BALANCE POP STOP: reads the balance of the coinbase of the blocks built here.
    protected static readonly Address CoinbaseBalanceReader = new("0x00000000000000000000000000000000004e4d14");
    // CALL(1000, CoinbaseBalanceReader, 0, 0, 0, 0, 0) POP STOP: enough gas for a warm balance read only.
    protected static readonly Address LimitedCaller = new("0x00000000000000000000000000000000004e4d15");
    // EXTCODECOPY(F, 0, 0, 0) STOP: accesses the coinbase of the blocks built here without reading it.
    protected static readonly Address CoinbaseCodeCopier = new("0x00000000000000000000000000000000004e4d16");
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

    /// <param name="configure">Registrations made after the production ones, as a plugin's would be.</param>
    protected void Initialize(bool handoff, Action<ContainerBuilder>? configure = null)
    {
        BlocksConfig blocksConfig = new()
        {
            PreWarming = PreWarmMode.Block,
            PreWarmStateConcurrency = 3,
            ProcessingCores = ProcessingCores.All,
            PreWarmHandoff = handoff
        };
        ConfigureBlocks(blocksConfig);
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new TestNethermindModule(blocksConfig))
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Spec))
            .AddSingleton<IStateHeaderProvider>(new Parents(_headers));
        configure?.Invoke(builder);
        _container = builder.Build();

        IMainProcessingModule[] mainModules = _container.Resolve<IMainProcessingModule[]>();
        IWorldStateManager worldStateManager = _container.Resolve<IWorldStateManager>();
        IWorldStateScopeProvider scopeProvider = WrapMainScopeProvider(worldStateManager.GlobalWorldState);
        ProcessingScope = _container.BeginLifetimeScope(b =>
        {
            b.RegisterInstance(scopeProvider).As<IWorldStateScopeProvider>().ExternallyOwned();
            b.RegisterInstance(WrapWorldStateManager(worldStateManager)).As<IWorldStateManager>().ExternallyOwned();
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
            // BALANCE(C) POP; SSTORE(0, 1); SSTORE(0, 0); SSTORE(1, 1); STOP
            Deploy(worldState, Guarded, [0x73, .. TestItem.AddressC.Bytes, 0x31, 0x50, 0x60, 0x01, 0x5F, 0x55, 0x5F, 0x5F, 0x55, 0x60, 0x01, 0x60, 0x01, 0x55, 0x00], 0);
            Deploy(worldState, CoinbaseReader, [0x41, 0x5F, 0x55, 0x00], 0);
            Deploy(worldState, TimestampReader, [0x42, 0x5F, 0x55, 0x00], 0);
            Deploy(worldState, GasLimitReader, [0x45, 0x5F, 0x55, 0x00], 0);
            Deploy(worldState, PrevRandaoReader, [0x44, 0x5F, 0x55, 0x00], 0);
            Deploy(worldState, CoinbaseBalanceReader, [0x73, .. TestItem.AddressF.Bytes, 0x31, 0x50, 0x00], 0);
            Deploy(worldState, LimitedCaller, [0x5F, 0x5F, 0x5F, 0x5F, 0x5F, 0x73, .. CoinbaseBalanceReader.Bytes, 0x61, 0x03, 0xE8, 0xF1, 0x50, 0x00], 0);
            Deploy(worldState, CoinbaseCodeCopier, [0x5F, 0x5F, 0x5F, 0x73, .. TestItem.AddressF.Bytes, 0x3C, 0x00], 0);
            Deploy(worldState, Child, ChildCode, 0x4e4d);
            worldState.Set(new StorageCell(Child, 0), 0x4e4d);
            worldState.Set(new StorageCell(Child, 1), 2);
            worldState.Set(new StorageCell(Child, ChildSlot), 0x4e4d);
            DeployGenesis(worldState);
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

    /// <summary>Adjusts the blocks config the fixture's container is built with.</summary>
    protected virtual void ConfigureBlocks(BlocksConfig config) { }

    /// <summary>Deploys a fixture's own accounts into the genesis state, before it is committed.</summary>
    protected virtual void DeployGenesis(IWorldState worldState) { }

    /// <summary>Decorates the scope provider block processing reads through, under the pre-block caches.</summary>
    protected virtual IWorldStateScopeProvider WrapMainScopeProvider(IWorldStateScopeProvider provider) => provider;

    /// <summary>Decorates the world state manager of block processing's scope, which the prewarmer's envs create their world states from.</summary>
    protected virtual IWorldStateManager WrapWorldStateManager(IWorldStateManager manager) => manager;

    /// <summary>The state reader of the fixture's container, which reads past every cache.</summary>
    protected IStateReader StateReader => _container.Resolve<IWorldStateManager>().GlobalStateReader;

    [TearDown]
    public void TearDown()
    {
        ProcessingScope?.Dispose();
        _container?.Dispose();
    }

    protected sealed record Run(Hash256 StateRoot, TxReceipt[] Receipts, TransactionResult[] Results, (int Replayed, int Rejected, int Missing) Tally);

    /// <summary>Processes the block by execution, then warmed and handed off, and requires the same results, root and receipts.</summary>
    protected Run Handoff(Block block, IBlockTracer? otherTracer = null, Action<IWorldState>? beforeTransactions = null) =>
        Handoff(block, PreWarmer, ProductionAdapter, otherTracer, beforeTransactions);

    protected Run Handoff(Block block, BlockCachePreWarmer preWarmer, PrewarmerTxAdapter adapter, IBlockTracer? otherTracer = null,
        Action<IWorldState>? beforeTransactions = null)
    {
        Run executed = Process(block, adapter: null, beforeTransactions: beforeTransactions);
        RunPreWarmCaches(preWarmer, block);
        Run run = Process(block, adapter, otherTracer: otherTracer, beforeTransactions: beforeTransactions);

        AssertSameRun(run, executed);
        return run;
    }

    /// <summary>
    /// Processes the block by execution, then with its prewarming session open while the production adapter processes
    /// it, as block processing does, and requires the same results, root and receipts.
    /// </summary>
    /// <param name="afterSession">Run once the transactions are executed and the session has ended, before the block is committed.</param>
    protected Run HandoffWhileWarming(Block block, Action? afterSession = null, Action<IWorldState>? beforeTransactions = null)
    {
        Run executed = Process(block, adapter: null, beforeTransactions: beforeTransactions);
        Run run = ProcessWithSession(block, afterSession, beforeTransactions);
        AssertSameRun(run, executed);
        return run;
    }

    /// <summary>Processes the block with its prewarming session open, ending the session once the transactions are executed.</summary>
    protected Run ProcessWithSession(Block block, Action? afterSession = null, Action<IWorldState>? beforeTransactions = null)
    {
        IWorldState worldState = ProcessingScope.Resolve<IWorldState>();
        using (worldState.BeginScope(Parent))
        {
            IDisposable? session = PreWarmer.PreWarmCaches(block, Parent, Spec);
            try
            {
                return ProcessInScope(block, ProductionAdapter, beforeTransactions: beforeTransactions, transactionsExecuted: () =>
                {
                    Interlocked.Exchange(ref session, null)?.Dispose();
                    afterSession?.Invoke();
                });
            }
            finally
            {
                session?.Dispose();
            }
        }
    }

    protected static void AssertSameRun(Run run, Run executed)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Results, Is.EqualTo(executed.Results));
            Assert.That(run.StateRoot, Is.EqualTo(executed.StateRoot));
        }

        AssertSameReceipts(run.Receipts, executed.Receipts);
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
    protected Run Process(Block block, PrewarmerTxAdapter? adapter, BlockHeader? parent = null, IBlockTracer? otherTracer = null,
        Action<IWorldState>? beforeTransactions = null)
    {
        IWorldState worldState = ProcessingScope.Resolve<IWorldState>();
        using (worldState.BeginScope(parent ?? Parent)) return ProcessInScope(block, adapter, otherTracer, beforeTransactions: beforeTransactions);
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
                beforeTransaction: index => Assert.That(SpinWait.SpinUntil(() => ready(footprints, index), TimeSpan.FromSeconds(10)), Is.True,
                    $"the footprints were not ready before transaction {index}"),
                transactionsExecuted: () => session?.Dispose());
        }
    }

    private Run ProcessInScope(Block block, PrewarmerTxAdapter? adapter, IBlockTracer? otherTracer = null,
        Action<int>? beforeTransaction = null, Action? transactionsExecuted = null, Action<IWorldState>? beforeTransactions = null)
    {
        IWorldState worldState = ProcessingScope.Resolve<IWorldState>();
        Block processing = new(block.Header.CloneForProcessing(), block.Body);
        (int Replayed, int Rejected, int Missing) before = adapter?.Tally ?? default;
        ITransactionProcessorAdapter transactions = (ITransactionProcessorAdapter?)adapter ?? new ExecuteTransactionProcessorAdapter(ProcessingScope.Resolve<ITransactionProcessor>());
        beforeTransactions?.Invoke(worldState);
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

    /// <summary>The cache entries whose value is not the parent state's, read past every cache.</summary>
    protected List<string> EntriesNotAtTheParent()
    {
        PreBlockCaches caches = ProcessingScope.Resolve<PreBlockCaches>();
        IStateReader reader = StateReader;
        List<string> wrong = [];
        foreach ((AddressAsKey key, Account? account) in caches.StateCache.SnapshotForTests())
        {
            bool exists = reader.TryGetAccount(Parent, key.Value, out AccountStruct parent);
            if (account is null ? exists : !exists || !account.ToStruct().Equals(parent))
            {
                wrong.Add($"account {key.Value}: {Describe(account?.ToStruct())} cached, {Describe(exists ? parent : null)} at the parent");
            }
        }

        foreach ((StorageCell cell, UInt256 value) in caches.StorageCache.SnapshotForTests())
        {
            reader.GetStorage(Parent, cell.Address, in cell.Index, out UInt256 parent);
            if (value != parent) wrong.Add($"slot {cell}: {value} cached, {parent} at the parent");
        }

        return wrong;
    }

    private static string Describe(AccountStruct? account) => account is { } a
        ? $"nonce {a.Nonce} balance {a.Balance} storage {a.StorageRoot} code {a.CodeHash}"
        : "none";

    /// <summary>A block of the cold tail's shapes, from <paramref name="rng"/>; <paramref name="heavyGas"/> splits a sender's claims.</summary>
    protected Block RandomColdTailBlock(Random rng, ulong heavyGas = 2_100_000)
    {
        PrivateKey[] senders = [TestItem.PrivateKeyA, TestItem.PrivateKeyB, TestItem.PrivateKeyC, TestItem.PrivateKeyD];
        ulong[] nonces = new ulong[senders.Length];
        int count = rng.Next(3, 11);
        Transaction[] transactions = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            int s = rng.Next(senders.Length);
            PrivateKey sender = senders[s];
            ulong nonce = nonces[s]++;
            transactions[i] = rng.Next(12) switch
            {
                0 or 1 => BatchClaim(sender, nonce, rng.Next(1, 10), rng.Next(2) == 0 ? 400_000ul : heavyGas),
                2 => Claim(sender, nonce, rng.Next(1, 6), (ulong)rng.Next(0, 4) * 16),
                3 => Call(sender, nonce, ColdTailContracts.Deleter, 200_000),
                4 => Call(sender, nonce, ColdTailContracts.Deleter, 200_000, data: [1]),
                5 => Walk(sender, nonce, rng.Next(2) == 0 ? 0x4e4dul : (ulong)rng.Next(0x100, 0x140), rng.Next(1, 12)),
                6 => Call(sender, nonce, ColdTailContracts.Chain, 500_000),
                7 => Call(sender, nonce, ColdTailContracts.Brancher),
                8 => Transfer(sender, nonce, ColdTailContracts.Gate, 1.Wei),
                9 => Call(sender, nonce, rng.Next(2) == 0 ? ColdTailContracts.SwapThenCheck : ColdTailContracts.CheckThenSwap,
                    data: rng.Next(2) == 0 ? Array.Empty<byte>() : new byte[] { 2 }),
                10 => Call(sender, nonce, rng.Next(2) == 0 ? Caller : Reverter),
                _ => Call(sender, nonce, Counter),
            };
        }

        return BuildBlock(transactions);
    }

    protected static Transaction BatchClaim(PrivateKey sender, ulong nonce, int count, ulong gasLimit) =>
        Call(sender, nonce, ColdTailContracts.Batch, gasLimit, ColdTailContracts.Word((ulong)count));

    protected static Transaction Claim(PrivateKey sender, ulong nonce, int proxies, ulong firstSalt) =>
        Call(sender, nonce, ColdTailContracts.Claimer, 1_500_000, ColdTailContracts.Words((ulong)proxies, firstSalt));

    protected static Transaction Walk(PrivateKey sender, ulong nonce, ulong start, int count) =>
        Call(sender, nonce, ColdTailContracts.Walker, 400_000, ColdTailContracts.Words(start, (ulong)count));

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

/// <summary>
/// Runs the mempool pass records on the parent, before the block arrives, are taken over by the block like its own
/// warm runs, but only where the block context they ran on is the block's.
/// </summary>
[TestFixture]
public class PrewarmerMempoolHandoffTests() : PrewarmerHandoffTestBase(Osaka.Instance)
{
    [Test]
    public void Runs_recorded_on_the_parent_are_taken_over_into_the_state_and_receipts_of_executing_the_block()
    {
        (Run run, bool[] taken) = MempoolHandoff(BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Counter),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 1.Wei),
            Call(TestItem.PrivateKeyD, 0, Logger),
            Create(TestItem.PrivateKeyC, 0, DeployCode)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(taken, Is.All.True);
            Assert.That(run.Tally.Replayed, Is.EqualTo(4));
        }
    }

    public enum ContextField { Coinbase, Timestamp, GasLimit, PrevRandao }

    [Test]
    public void A_run_that_read_the_block_context_is_taken_over_only_where_it_was_predicted([Values] ContextField field, [Values] bool predicted)
    {
        Address reader = field switch
        {
            ContextField.Coinbase => CoinbaseReader,
            ContextField.Timestamp => TimestampReader,
            ContextField.GasLimit => GasLimitReader,
            _ => PrevRandaoReader
        };
        Block block = BuildBlock(Call(TestItem.PrivateKeyA, 0, reader), Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressD, 1.Wei), Unrelated());

        (_, bool[] taken) = MempoolHandoff(block, predicted ? null : static header =>
        {
            header.Timestamp += 12;
            header.GasLimit += 1;
        });

        // The coinbase and the random value are never known before the block; a run that did not read the context holds anyway.
        bool known = field is ContextField.Timestamp or ContextField.GasLimit;
        Assert.That(taken, Is.EqualTo(new[] { known && predicted, true, true }));
    }

    [Test]
    public void A_run_touching_the_blocks_coinbase_is_executed()
    {
        // Execution warms the coinbase (EIP-3651), which the run did not.
        (_, bool[] taken) = MempoolHandoff(BuildBlock(
            Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressF, 1.Wei),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressD, 1.Wei),
            Unrelated()));

        Assert.That(taken, Is.EqualTo(new[] { false, true, true }));
    }

    public enum ColdCoinbaseAccess { OutOfGas, OutOfGasInChildCall, EmptyCodeCopy }

    [Test]
    public void A_run_that_paid_for_accessing_the_cold_coinbase_is_executed([Values] ColdCoinbaseAccess access)
    {
        // The block's coinbase is warm (EIP-3651), so the access costs less there, with no state value in the run to show it.
        Transaction tx = access switch
        {
            ColdCoinbaseAccess.OutOfGas => Call(TestItem.PrivateKeyA, 0, CoinbaseBalanceReader, gasLimit: 22_000),
            ColdCoinbaseAccess.OutOfGasInChildCall => Call(TestItem.PrivateKeyA, 0, LimitedCaller),
            _ => Call(TestItem.PrivateKeyA, 0, CoinbaseCodeCopier)
        };

        (_, bool[] taken) = MempoolHandoff(BuildBlock(tx, Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressD, 1.Wei), Unrelated()));

        Assert.That(taken, Is.EqualTo(new[] { false, true, true }));
    }

    [Test]
    public void A_run_paying_the_coinbase_nothing_is_executed()
    {
        // With no fee the coinbase is only touched where it exists, which the run on another coinbase does not tell.
        Transaction free = Build.A.Transaction.WithType(TxType.EIP1559).WithNonce(0).WithTo(TestItem.AddressD).WithValue(1.Wei)
            .WithGasLimit(GasCostOf.Transaction).WithMaxFeePerGas(2.GWei).WithMaxPriorityFeePerGas(0)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;

        (_, bool[] taken) = MempoolHandoff(BuildBlock(free, Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressD, 1.Wei), Unrelated()));

        Assert.That(taken, Is.EqualTo(new[] { false, true, true }));
    }

    private static IEnumerable<TestCaseData> RecordingSpecs()
    {
        yield return new TestCaseData(Osaka.Instance, 2).SetName("Recorded before block access lists");
        yield return new TestCaseData(Amsterdam.Instance, 0).SetName("Not recorded with block access lists");
        yield return new TestCaseData(SpuriousDragon.Instance, 0).SetName("Not recorded before receipt status codes");
    }

    [TestCaseSource(nameof(RecordingSpecs))]
    public void Runs_are_recorded_only_for_a_next_block_the_footprints_apply_to(IReleaseSpec spec, int expected)
    {
        Block block = BuildBlock(Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressD, 1.Wei), Unrelated());

        PreWarmer.RunSpeculativePreWarm(Parent, spec, Predicted(block, null), () => PreWarmer.SpeculativeMarkerPublished);

        Assert.That(PreWarmer.MempoolRunsRecorded, Is.EqualTo(expected));
    }

    [Test]
    public void The_predicted_coinbase_is_still_warmed_while_the_runs_pay_another()
    {
        Block delta = Predicted(BuildBlock(Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressD, 1.Wei), Unrelated()),
            static header => header.Beneficiary = Reverter);

        PreWarmer.RunSpeculativePreWarm(Parent, Spec, delta, () => PreWarmer.SpeculativeMarkerPublished);

        Assert.That(ProcessingScope.Resolve<PreBlockCaches>().StateCache.TryGetValue(Reverter, out _), Is.True);
    }

    [Test]
    public void The_transaction_workers_warm_while_the_runs_are_taken_over()
    {
        Block block = BuildBlock(Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressC, 1.Wei),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 1.Wei), Unrelated());
        Run executed = Process(block, adapter: null);
        // The mempool pass never saw the last transaction, so a worker warms it whatever is taken over.
        PreWarmer.RunSpeculativePreWarm(Parent, Spec, Predicted(BuildBlock(block.Transactions[0], block.Transactions[1]), null),
            () => PreWarmer.SpeculativeMarkerPublished);

        // The taking over is held until a warm run stores a footprint.
        bool warmedWhileTakingOver = false;
        PreWarmer.Seeded = () => warmedWhileTakingOver = SpinWait.SpinUntil(WarmRunStored, TimeSpan.FromSeconds(10));
        RunPreWarmCaches(PreWarmer, block);
        PreWarmer.Seeded = null;
        Run run = Process(block, ProductionAdapter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(warmedWhileTakingOver, Is.True);
            Assert.That(run.Results, Is.EqualTo(executed.Results));
            Assert.That(run.StateRoot, Is.EqualTo(executed.StateRoot));
            Assert.That(run.Tally.Replayed, Is.EqualTo(3));
        }

        AssertSameReceipts(run.Receipts, executed.Receipts);

        bool WarmRunStored() => PreWarmer.Footprints is { } footprints
            && Enumerable.Range(0, footprints.Count).Any(i => footprints.Get(i) is { FromMempool: false });
    }

    [Test]
    public void A_transaction_whose_sender_lands_after_its_run_was_taken_over_is_left_to_the_run()
    {
        Transaction late = Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 1.Wei);
        Block block = BuildBlock(Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressC, 1.Wei), late, Unrelated());
        Run executed = Process(block, adapter: null);
        PreBlockCaches caches = ProcessingScope.Resolve<PreBlockCaches>();
        using BlockCachePreWarmer preWarmer = new(
            new BlockCachePreWarmer.ReadOnlyTxProcessingEnvPooledObjectPolicy(ProcessingScope.Resolve<PrewarmerEnvFactory>(), caches),
            minPoolSize: 4,
            concurrency: 3,
            parallelExecutionBatchRead: true,
            ProcessingScope.Resolve<NodeStorageCache>(),
            caches,
            LimboLogs.Instance,
            senderRecovery: new LateSender(late, TestItem.AddressB),
            handoff: true);
        PrewarmerTxAdapter adapter = new(
            new ExecuteTransactionProcessorAdapter(ProcessingScope.Resolve<ITransactionProcessor>()),
            preWarmer,
            new PrewarmerState(caches, isPrewarmer: false),
            ProcessingScope.Resolve<IWorldState>(),
            LimboLogs.Instance);
        preWarmer.RunSpeculativePreWarm(Parent, Spec, Predicted(block, null), () => preWarmer.SpeculativeMarkerPublished);
        late.SenderAddress = null;

        // The runs are stored before the workers start, so the late transaction is the only one a worker could warm.
        using ManualResetEventSlim seeded = new(initialState: false);
        preWarmer.Seeded = seeded.Set;
        preWarmer.BeforeTransactionWorkers = () => seeded.Wait(TimeSpan.FromSeconds(10));
        RunPreWarmCaches(preWarmer, block);
        bool[] taken = [.. Enumerable.Range(0, block.Transactions.Length).Select(i => preWarmer.Footprints?.Get(i)?.FromMempool == true)];
        Run run = Process(block, adapter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(late.SenderAddress, Is.EqualTo(TestItem.AddressB), "precondition: the sender landed while the block was warmed");
            Assert.That(taken, Is.All.True);
            Assert.That(run.Results, Is.EqualTo(executed.Results));
            Assert.That(run.StateRoot, Is.EqualTo(executed.StateRoot));
            Assert.That(run.Tally.Replayed, Is.EqualTo(3));
        }
    }

    /// <summary>
    /// The takeover belongs to the warm pass, so the pass does not end while the takeover still runs. The takeover is held
    /// until the pass ends or the coordinator waits at the join, so a pass that ends without joining it ends while it is
    /// held.
    /// </summary>
    [Test]
    public void The_warm_pass_ends_only_once_the_runs_are_taken_over()
    {
        Block block = BuildBlock(Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressC, 1.Wei),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 1.Wei), Unrelated());
        PreWarmer.RunSpeculativePreWarm(Parent, Spec, Predicted(block, null), () => PreWarmer.SpeculativeMarkerPublished);

        BlockFootprints? footprints = null;
        Thread? joining = null;
        bool heldAtJoin = false;
        bool? endedWhileTakingOver = null;
        PreWarmer.BeforeSeedingJoin = () => Volatile.Write(ref joining, Thread.CurrentThread);
        PreWarmer.Seeded = () =>
        {
            footprints = PreWarmer.Footprints;
            // The timeout only guards against a deadlock; what is asserted is the order.
            heldAtJoin = SpinWait.SpinUntil(() => footprints?.WarmPassEnded == true || WaitsAtJoin(), TimeSpan.FromSeconds(10));
            endedWhileTakingOver = footprints?.WarmPassEnded;
        };
        RunPreWarmCaches(PreWarmer, block);
        PreWarmer.Seeded = null;
        PreWarmer.BeforeSeedingJoin = null;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(footprints, Is.Not.Null, "precondition: the takeover ran on the block's footprints");
            Assert.That(heldAtJoin, Is.True, "precondition: the takeover was held until the pass ended or the coordinator waited at the join");
            Assert.That(endedWhileTakingOver, Is.False, "the warm pass ended while the takeover was still running");
            Assert.That(footprints?.WarmPassEnded, Is.True, "the warm pass ended once the takeover returned");
        }

        // Past the join boundary the coordinator blocks nowhere but in the join before the pass ends; a takeover it runs
        // on its own thread from there is inside the join.
        bool WaitsAtJoin() => Volatile.Read(ref joining) is { } coordinator
            && (coordinator == Thread.CurrentThread || (coordinator.ThreadState & ThreadState.WaitSleepJoin) != 0);
    }

    /// <summary>
    /// A sender's job is left to the mempool runs only when every one of its transactions was taken over; a transaction
    /// of that sender the mempool pass never saw is still warmed, and its run is replayed.
    /// </summary>
    [Test]
    public void A_job_only_partly_taken_over_is_still_warmed()
    {
        Block block = BuildBlock(Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressC, 1.Wei),
            Transfer(TestItem.PrivateKeyA, 1, TestItem.AddressC, 1.Wei), Unrelated());
        Run executed = Process(block, adapter: null);
        // The mempool pass saw only the sender's first transaction.
        PreWarmer.RunSpeculativePreWarm(Parent, Spec, Predicted(BuildBlock(block.Transactions[0]), null),
            () => PreWarmer.SpeculativeMarkerPublished);

        using ManualResetEventSlim seeded = new(initialState: false);
        bool seededFirst = false;
        PreWarmer.Seeded = seeded.Set;
        PreWarmer.BeforeTransactionWorkers = () => seededFirst = seeded.Wait(TimeSpan.FromSeconds(10));
        RunPreWarmCaches(PreWarmer, block);
        PreWarmer.Seeded = null;
        PreWarmer.BeforeTransactionWorkers = null;
        TransactionFootprint? second = PreWarmer.Footprints?.Get(1);
        Run run = Process(block, ProductionAdapter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(seededFirst, Is.True, "precondition: the runs were taken over before the transaction workers started");
            Assert.That(second, Is.Not.Null, "the transaction the mempool pass never saw was not warmed");
            Assert.That(run.Results, Is.EqualTo(executed.Results));
            Assert.That(run.StateRoot, Is.EqualTo(executed.StateRoot));
            Assert.That(run.Tally.Replayed, Is.EqualTo(3));
        }
    }

    /// <summary>A recovery in flight whose one sender lands only once a warm worker waits for it, after the block's jobs were taken.</summary>
    private sealed class LateSender(Transaction tx, Address sender) : ISenderRecoveryTracker, ISenderRecoveryProgress
    {
        private int _recovered;
        private int _landing;

        public ISenderRecoveryProgress GetInFlight(Transaction[] txs) => this;
        public int Recovered => Volatile.Read(ref _recovered);
        public bool IsCompleted => false;

        public bool WaitForCompletion(int millisecondsTimeout)
        {
            if (Interlocked.Exchange(ref _landing, 1) == 0)
            {
                tx.SenderAddress = sender;
                Volatile.Write(ref _recovered, 1);
            }

            return false;
        }
    }

    [Test]
    public void Runs_are_warmed_again_where_the_caches_did_not_carry_over()
    {
        // Taking a run over reads its preconditions from the caches, which only the warm pass refills after a clear.
        (_, bool[] taken) = MempoolHandoff(BuildBlock(Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressD, 1.Wei),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressD, 1.Wei), Unrelated()), carried: false);

        Assert.That(taken, Has.None.True);
    }

    // A block of fewer transactions is not warmed, so it takes no runs either.
    private static Transaction Unrelated() => Transfer(TestItem.PrivateKeyD, 0, TestItem.AddressC, 1.Wei);

    /// <summary>
    /// Warms copies of the block's transactions in a mempool pass on a predicted header, then processes the block as
    /// <see cref="PrewarmerHandoffTestBase.Handoff(Block, Nethermind.Evm.Tracing.IBlockTracer?)"/> does.
    /// </summary>
    /// <returns>The run, and per transaction whether a mempool run was stored for block processing to take over.</returns>
    private (Run Run, bool[] Taken) MempoolHandoff(Block block, Action<BlockHeader>? mispredict = null, bool carried = true)
    {
        Run executed = Process(block, adapter: null);

        PreWarmer.RunSpeculativePreWarm(Parent, Spec, Predicted(block, mispredict), () => PreWarmer.SpeculativeMarkerPublished);
        if (!carried) ProcessingScope.Resolve<PreBlockCaches>().PrepareFor(Keccak.Zero);
        // Taken over alongside the warms, the runs are stored first here, so what is taken does not depend on which
        // gets to a transaction first.
        using ManualResetEventSlim seeded = new(initialState: false);
        bool seededFirst = false;
        PreWarmer.Seeded = seeded.Set;
        PreWarmer.BeforeTransactionWorkers = () => seededFirst = seeded.Wait(TimeSpan.FromSeconds(10));
        RunPreWarmCaches(PreWarmer, block);
        PreWarmer.Seeded = null;
        PreWarmer.BeforeTransactionWorkers = null;
        Assert.That(seededFirst, Is.True, "the runs were not taken over before the transaction workers started");
        bool[] taken = new bool[block.Transactions.Length];
        for (int i = 0; i < taken.Length; i++) taken[i] = PreWarmer.Footprints?.Get(i)?.FromMempool == true;

        Run run = Process(block, ProductionAdapter);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Results, Is.EqualTo(executed.Results));
            Assert.That(run.StateRoot, Is.EqualTo(executed.StateRoot));
        }

        AssertSameReceipts(run.Receipts, executed.Receipts);
        return (run, taken);
    }

    // The block the mempool pass predicts, as the mempool prewarmer builds it, of copies of the block's transactions.
    private Block Predicted(Block block, Action<BlockHeader>? mispredict)
    {
        BlockHeader header = Parent.CreateSimulatedChild(block.Timestamp);
        header.Beneficiary = Parent.GasBeneficiary ?? Address.Zero;
        header.BaseFeePerGas = block.BaseFeePerGas;
        header.ExcessBlobGas = block.Header.ExcessBlobGas;
        mispredict?.Invoke(header);

        Transaction[] copies = new Transaction[block.Transactions.Length];
        for (int i = 0; i < copies.Length; i++)
        {
            copies[i] = new Transaction();
            block.Transactions[i].CopyTo(copies[i], copyHash: true);
        }

        return new Block(header, new BlockBody(copies, [], null));
    }
}

/// <summary>Which recorded runs hold in the block that arrives, from what they read of the context they ran on.</summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class MempoolFootprintsTests
{
    private const ulong Fee = 0x4e4d;

    public enum Difference
    {
        None,
        TimestampUnread,
        GasLimitUnread,
        Parent,
        Number,
        BaseFee,
        ExcessBlobGas,
        SlotNumber,
        Spec,
        TimestampRead,
        GasLimitRead,
        CoinbaseRead,
        PrevRandaoRead,
        OutOfGas,
        CoinbaseRequired,
        PredictedCoinbaseRequired,
        CoinbaseSlotRead,
        CoinbaseChanged,
        NoFee,
        TwoFees,
        FeeOfAnotherKind,
        RecordedAgainWhenFull
    }

    [Test]
    public void A_run_is_stored_only_where_it_holds_in_the_block([Values] Difference difference)
    {
        BlockHeader head = Build.A.BlockHeader.WithNumber(10).WithHash(TestItem.KeccakA).TestObject;
        Transaction recordedTx = Build.A.Transaction.WithType(TxType.EIP1559).WithMaxFeePerGas(2.GWei).WithMaxPriorityFeePerGas(1.GWei)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction tx = new();
        recordedTx.CopyTo(tx, copyHash: true);

        Block block = Build.A.Block
            .WithParentHash(difference == Difference.Parent ? TestItem.KeccakB : head.Hash!)
            .WithNumber(difference == Difference.Number ? 12 : 11)
            .WithBeneficiary(TestItem.AddressF)
            .WithTimestamp(difference is Difference.TimestampRead or Difference.TimestampUnread ? 124UL : 112UL)
            .WithGasLimit(difference is Difference.GasLimitRead or Difference.GasLimitUnread ? 30_000_001UL : 30_000_000UL)
            .WithBaseFeePerGas(difference == Difference.BaseFee ? 8UL : 7UL)
            .WithExcessBlobGas(difference == Difference.ExcessBlobGas ? 1UL : 0UL)
            .WithSlotNumber(difference == Difference.SlotNumber ? 2UL : null)
            .WithTransactions(tx)
            .TestObject;
        BlockHeader predicted = Build.A.BlockHeader.WithParentHash(head.Hash!).WithNumber(11).WithTimestamp(112).WithGasLimit(30_000_000)
            .WithBaseFee(7).WithExcessBlobGas(0).TestObject;

        MempoolFootprints recorded = new(head);
        Address coinbase = TestItem.AddressF;
        List<StateEffect> effects =
        [
            new() { Kind = EffectKind.AddToBalance, Cell = new StorageCell(TestItem.AddressC, 0), Value = 1 },
            new()
            {
                Kind = difference == Difference.FeeOfAnotherKind ? EffectKind.AddToBalance : EffectKind.AddToBalanceAndCreateIfNotExists,
                Cell = new StorageCell(recorded.Coinbase, 0),
                Value = Fee
            }
        ];
        if (difference == Difference.NoFee) effects.RemoveAt(1);
        if (difference == Difference.TwoFees) effects.Add(effects[1]);
        if (difference == Difference.CoinbaseChanged) effects.Add(new StateEffect { Kind = EffectKind.AddToBalance, Cell = new StorageCell(coinbase, 0), Value = 1 });

        AccountPrecondition[] accounts = difference switch
        {
            Difference.CoinbaseRequired => [new AccountPrecondition { Address = coinbase, Fields = AccountFields.Balance }],
            Difference.PredictedCoinbaseRequired => [new AccountPrecondition { Address = recorded.Coinbase, Fields = AccountFields.Existence }],
            _ => []
        };
        SlotPrecondition[] slots = difference == Difference.CoinbaseSlotRead ? [new SlotPrecondition { Cell = new StorageCell(coinbase, 1), Read = true }] : [];
        BlockContextReads reads = difference switch
        {
            Difference.TimestampRead => BlockContextReads.Timestamp,
            Difference.GasLimitRead => BlockContextReads.GasLimit,
            Difference.CoinbaseRead => BlockContextReads.Coinbase,
            Difference.PrevRandaoRead => BlockContextReads.PrevRandao,
            Difference.OutOfGas => BlockContextReads.OutOfGas,
            _ => BlockContextReads.None
        };
        TransactionFootprint footprint = new(recordedTx, accounts, slots, [.. effects], default, default, default) { ContextReads = reads };

        if (difference == Difference.RecordedAgainWhenFull)
        {
            // A later pass replaces a run that no longer holds even once no other transaction is taken.
            recorded.Record(footprint, predicted, Prague.Instance);
            for (int i = 1; i < MempoolFootprints.MaxEntries; i++)
            {
                Transaction other = Build.A.Transaction.WithNonce((ulong)i).WithHash(Keccak.Compute(BitConverter.GetBytes(i))).TestObject;
                recorded.Record(new TransactionFootprint(other, [], [], [], default, default, default), predicted, Osaka.Instance);
            }
        }

        recorded.Record(footprint, predicted, Osaka.Instance);
        BlockFootprints footprints = new(block);
        int seeded = recorded.SeedWhereEmpty(block, difference == Difference.Spec ? Prague.Instance : Osaka.Instance, footprints, CancellationToken.None);

        bool holds = difference is Difference.None or Difference.TimestampUnread or Difference.GasLimitUnread or Difference.RecordedAgainWhenFull;
        TransactionFootprint? stored = footprints.Get(0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored is not null, Is.EqualTo(holds));
            Assert.That(seeded, Is.EqualTo(holds ? 1 : 0));
        }

        if (stored is null) return;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Transaction, Is.SameAs(tx), "taken as the block's own transaction");
            Assert.That(stored.FromMempool, Is.True);
            Assert.That(stored.Effects[1].Address, Is.EqualTo(coinbase), "the fee goes to the block's coinbase");
            Assert.That(stored.Effects[1].Value, Is.EqualTo((UInt256)Fee));
            Assert.That(stored.Effects[0].Address, Is.EqualTo(TestItem.AddressC));
            Assert.That(footprint.Effects[1].Address, Is.EqualTo(recorded.Coinbase), "the recorded run is left as it was");
        }
    }

    [Test]
    public void Runs_past_the_cap_are_not_kept()
    {
        MempoolFootprints recorded = new(Build.A.BlockHeader.WithHash(TestItem.KeccakA).TestObject);
        BlockHeader predicted = Build.A.BlockHeader.TestObject;
        for (int i = 0; i <= MempoolFootprints.MaxEntries; i++)
        {
            Transaction tx = Build.A.Transaction.WithNonce((ulong)i).WithHash(Keccak.Compute(BitConverter.GetBytes(i))).TestObject;
            recorded.Record(new TransactionFootprint(tx, [], [], [], default, default, default), predicted, Osaka.Instance);
        }

        Assert.That(recorded.Count, Is.EqualTo(MempoolFootprints.MaxEntries));
    }

    [Test]
    public void Runs_are_taken_over_alongside_the_warms_only_where_no_footprint_is_stored_yet()
    {
        BlockHeader head = Build.A.BlockHeader.WithNumber(10).WithHash(TestItem.KeccakA).TestObject;
        BlockHeader predicted = Build.A.BlockHeader.WithParentHash(head.Hash!).WithNumber(11).WithTimestamp(112).WithGasLimit(30_000_000)
            .WithBaseFee(7).WithExcessBlobGas(0).TestObject;
        MempoolFootprints recorded = new(head);
        Transaction[] txs = new Transaction[3];
        for (int i = 0; i < txs.Length; i++)
        {
            Transaction recordedTx = Build.A.Transaction.WithType(TxType.EIP1559).WithMaxFeePerGas(2.GWei).WithMaxPriorityFeePerGas(1.GWei)
                .SignedAndResolved(TestItem.PrivateKeys[i]).TestObject;
            StateEffect fee = new() { Kind = EffectKind.AddToBalanceAndCreateIfNotExists, Cell = new StorageCell(recorded.Coinbase, 0), Value = Fee };
            recorded.Record(new TransactionFootprint(recordedTx, [], [], [fee], default, default, default), predicted, Osaka.Instance);
            txs[i] = new Transaction();
            recordedTx.CopyTo(txs[i], copyHash: true);
        }

        Block block = Build.A.Block.WithParentHash(head.Hash!).WithNumber(11).WithBeneficiary(TestItem.AddressF).WithTimestamp(112)
            .WithGasLimit(30_000_000).WithBaseFeePerGas(7).WithExcessBlobGas(0).WithTransactions(txs).TestObject;
        BlockFootprints footprints = new(block);
        TransactionFootprint warmed = new(txs[1], [], [], [], default, default, default);
        footprints.Store(1, warmed);

        int stored = recorded.SeedWhereEmpty(block, Osaka.Instance, footprints, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored, Is.EqualTo(2));
            Assert.That(footprints.Get(0)?.FromMempool, Is.True);
            Assert.That(footprints.Get(1), Is.SameAs(warmed), "the warm run that got there first keeps its footprint");
            Assert.That(footprints.Get(2)?.FromMempool, Is.True);
        }
    }

    [Test]
    public void Taking_over_stops_at_the_next_transaction_once_the_pass_is_cancelled()
    {
        const int count = 4;
        BlockHeader head = Build.A.BlockHeader.WithNumber(10).WithHash(TestItem.KeccakA).TestObject;
        BlockHeader predicted = Build.A.BlockHeader.WithParentHash(head.Hash!).WithNumber(11).WithTimestamp(112).WithGasLimit(30_000_000)
            .WithBaseFee(7).WithExcessBlobGas(0).TestObject;
        MempoolFootprints recorded = new(head);
        Transaction[] signed = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            signed[i] = Build.A.Transaction.WithType(TxType.EIP1559).WithMaxFeePerGas(2.GWei).WithMaxPriorityFeePerGas(1.GWei)
                .SignedAndResolved(TestItem.PrivateKeys[i]).TestObject;
            StateEffect fee = new() { Kind = EffectKind.AddToBalanceAndCreateIfNotExists, Cell = new StorageCell(recorded.Coinbase, 0), Value = Fee };
            recorded.Record(new TransactionFootprint(signed[i], [], [], [fee], default, default, default), predicted, Osaka.Instance);
        }

        // Decoded as a payload is: a transaction's hash is computed under its own lock on the first read.
        Transaction[] txs = TxsDecoder.DecodeTxs(
            [.. signed.Select(static tx => Rlp.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes)], skipErrors: false).Transactions;
        Block block = Build.A.Block.WithParentHash(head.Hash!).WithNumber(11).WithBeneficiary(TestItem.AddressF).WithTimestamp(112)
            .WithGasLimit(30_000_000).WithBaseFeePerGas(7).WithExcessBlobGas(0).WithTransactions(txs).TestObject;
        BlockFootprints footprints = new(block);
        using CancellationTokenSource cancellation = new();
        Task<int> takingOver;
        bool firstTaken;
        // Held at the second transaction's hash until the pass is cancelled, once the first run is taken over.
        lock (txs[1])
        {
            takingOver = Task.Run(() => recorded.SeedWhereEmpty(block, Osaka.Instance, footprints, cancellation.Token));
            firstTaken = SpinWait.SpinUntil(() => footprints.Get(0) is not null, TimeSpan.FromSeconds(10));
            cancellation.Cancel();
        }

        int stored = takingOver.GetAwaiter().GetResult();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstTaken, Is.True, "precondition: the first run is taken over before the pass is cancelled");
            // The second transaction was already being taken over when the pass was cancelled.
            Assert.That(footprints.Get(2), Is.Null);
            Assert.That(footprints.Get(3), Is.Null);
            Assert.That(stored, Is.EqualTo(Enumerable.Range(0, count).Count(i => footprints.Get(i) is not null)));
        }
    }
}

/// <summary>
/// The mempool pass's runs taken over while the block's warm runs store theirs: a run taken over never replaces a stored
/// footprint, a warm run replaces a run taken over, and what is indexed stays what is stored.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class BlockFootprintsSeedingTests
{
    [Test]
    public void A_seed_is_stored_only_where_no_footprint_is_and_a_stored_footprint_replaces_a_seed()
    {
        (BlockFootprints footprints, Transaction[] txs) = PrewarmerHandoffMechanicsTests.Footprints(3);
        StorageCell cell = new(TestItem.AddressC, 0x4e4d);
        TransactionFootprint warm = PrewarmerHandoffMechanicsTests.Footprint(txs[0], writes: [(cell, 1)]);
        TransactionFootprint seed = PrewarmerHandoffMechanicsTests.Footprint(txs[1], writes: [(cell, 2)]);
        TransactionFootprint replacing = PrewarmerHandoffMechanicsTests.Footprint(txs[1], writes: [(cell, 3)]);
        footprints.Store(0, warm);

        bool seededOverWarm = footprints.TryStoreIfEmpty(0, PrewarmerHandoffMechanicsTests.Footprint(txs[0], writes: [(cell, 4)]));
        bool seededEmpty = footprints.TryStoreIfEmpty(1, seed);
        TransactionFootprint? seededAt1 = footprints.Get(1);
        footprints.Store(1, replacing);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(seededOverWarm, Is.False);
            Assert.That(footprints.Get(0), Is.SameAs(warm));
            Assert.That(footprints.IndexedAt(0), Is.SameAs(warm));
            Assert.That(seededEmpty, Is.True);
            Assert.That(seededAt1, Is.SameAs(seed));
            Assert.That(footprints.Get(1), Is.SameAs(replacing));
            Assert.That(footprints.IndexedAt(1), Is.SameAs(replacing));
            Assert.That(footprints.ValueBefore(cell, 2), Is.EqualTo((UInt256)3));
        }
    }

    /// <remarks>
    /// Seeds, warm runs and later runs (a refresh, a run of a late sender) store at the same positions at once; each round
    /// is checked as a whole once they are done.
    /// </remarks>
    [Test]
    public void Seeds_and_warm_runs_storing_at_once_leave_each_position_the_footprint_indexed_for_it()
    {
        const int rounds = 300;
        const int count = 48;
        StorageCell[] cells = [.. Enumerable.Range(0, 4).Select(static i => new StorageCell(TestItem.AddressC, (UInt256)i))];
        int disagreeing = 0;
        int misplaced = 0;
        int versionsOff = 0;
        int missed = 0;
        for (int round = 0; round < rounds; round++)
        {
            (BlockFootprints footprints, Transaction[] txs) = PrewarmerHandoffMechanicsTests.Footprints(count);
            Random random = new(round);
            TransactionFootprint[] seeds = Runs(txs, cells, random);
            TransactionFootprint[] warms = Runs(txs, cells, random);
            TransactionFootprint[] later = Runs(txs, cells, random);
            bool[] warmed = [.. Enumerable.Range(0, count).Select(_ => random.Next(2) == 0)];
            int seedsStored = 0;
            int warmsStored = 0;
            int laterStored = 0;
            using Barrier start = new(3);
            Thread[] threads =
            [
                new(() =>
                {
                    start.SignalAndWait();
                    for (int i = 0; i < count; i++)
                    {
                        if (footprints.TryStoreIfEmpty(i, seeds[i])) seedsStored++;
                    }
                }),
                new(() =>
                {
                    start.SignalAndWait();
                    for (int i = 0; i < count; i++)
                    {
                        if (!warmed[i]) continue;
                        footprints.Store(i, warms[i]);
                        warmsStored++;
                    }
                }),
                new(() =>
                {
                    start.SignalAndWait();
                    // As a refresh does, only where a footprint is stored.
                    for (int i = count - 1; i >= 0; i--)
                    {
                        if (footprints.Get(i) is null) continue;
                        footprints.Store(i, later[i]);
                        laterStored++;
                    }
                })
            ];
            foreach (Thread thread in threads) thread.Start();
            foreach (Thread thread in threads) thread.Join();

            if (footprints.WritesVersion != seedsStored + warmsStored + laterStored) versionsOff++;
            HashSet<int> invalidated = [];
            while (footprints.TryTakeInvalidated(-1, out int position)) invalidated.Add(position);
            for (int i = 0; i < count; i++)
            {
                TransactionFootprint? stored = footprints.Get(i);
                if (!ReferenceEquals(stored, footprints.IndexedAt(i))) disagreeing++;
                // Where a warm run stored a footprint, the seed never replaced it.
                if (!ReferenceEquals(stored, later[i]) && !ReferenceEquals(stored, warmed[i] ? warms[i] : seeds[i])) misplaced++;
                if (stored is null) continue;
                foreach (ref readonly SlotPrecondition read in stored.Slots)
                {
                    // A footprint that read a slot at another value than the footprints before it leave is invalidated.
                    if (footprints.ValueBefore(read.Cell, i) is { } value && value != read.Value && !invalidated.Contains(i)) missed++;
                }
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(disagreeing, Is.Zero, "positions whose footprint is not the one indexed for them");
            Assert.That(misplaced, Is.Zero, "positions holding a seed over a warm run's footprint, or no footprint");
            Assert.That(versionsOff, Is.Zero, "rounds whose writes version does not count each footprint stored once");
            Assert.That(missed, Is.Zero, "outdated footprints left valid");
        }

        static TransactionFootprint[] Runs(Transaction[] txs, StorageCell[] cells, Random random) =>
        [
            .. txs.Select((tx, i) => PrewarmerHandoffMechanicsTests.Footprint(tx,
                reads: [(cells[i % cells.Length], (UInt256)random.Next(3))],
                writes: [(cells[(i + 1) % cells.Length], (UInt256)random.Next(3))]))
        ];
    }

    /// <remarks>
    /// Block processing reported executing a transaction and the writes were applied: a seed or a warm run that ends
    /// after that is not indexed for the position, so it is not stored there either, and a seed is not counted as stored.
    /// </remarks>
    [Test]
    public void Nothing_is_stored_at_a_position_block_processing_has_executed([Values] bool seed)
    {
        (BlockFootprints footprints, Transaction[] txs) = PrewarmerHandoffMechanicsTests.Footprints(2);
        StorageCell cell = new(TestItem.AddressC, 0x4e4d);
        footprints.QueueExecuted(0, new[] { (cell, (UInt256)7) });
        footprints.ApplyExecuted();
        TransactionFootprint late = PrewarmerHandoffMechanicsTests.Footprint(txs[0], writes: [(cell, 9)]);

        bool stored = true;
        if (seed) stored = footprints.TryStoreIfEmpty(0, late);
        else footprints.Store(0, late);

        using (Assert.EnterMultipleScope())
        {
            if (seed) Assert.That(stored, Is.False, "a seed for an executed position is not counted as stored");
            Assert.That(footprints.Get(0), Is.Null);
            Assert.That(footprints.IndexedAt(0), Is.Null);
            Assert.That(footprints.ValueBefore(cell, 1), Is.EqualTo((UInt256)7), "the executed writes stay indexed");
        }
    }

    /// <remarks>
    /// A refreshed run is excused reading one of its own seeded slots at another value only while no footprint was
    /// stored since the refresh read them: a seed taken over in between counts as one, a seed refused does not.
    /// </remarks>
    [Test]
    public void A_refreshed_run_is_checked_again_after_a_seed_is_stored_but_not_after_one_is_refused([Values] bool seedStored)
    {
        (BlockFootprints footprints, Transaction[] txs) = PrewarmerHandoffMechanicsTests.Footprints(3);
        StorageCell cell = new(TestItem.AddressC, 0x4e4d);
        footprints.Store(0, PrewarmerHandoffMechanicsTests.Footprint(txs[0], writes: [(cell, 0x4e4d)]));
        footprints.Store(1, PrewarmerHandoffMechanicsTests.Footprint(txs[1], reads: [(cell, UInt256.Zero)]));
        Assert.That(footprints.TryTakeInvalidated(-1, out _), Is.True, "precondition: the reader is invalidated");
        if (!seedStored) footprints.Store(2, PrewarmerHandoffMechanicsTests.Footprint(txs[2]));

        int seededAt = footprints.WritesVersion;
        bool stored = footprints.TryStoreIfEmpty(2, PrewarmerHandoffMechanicsTests.Footprint(txs[2]));
        footprints.Store(1, PrewarmerHandoffMechanicsTests.Footprint(txs[1], reads: [(cell, UInt256.Zero)]), seededAt);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored, Is.EqualTo(seedStored));
            Assert.That(footprints.TryTakeInvalidated(-1, out _), Is.EqualTo(seedStored));
        }
    }
}
