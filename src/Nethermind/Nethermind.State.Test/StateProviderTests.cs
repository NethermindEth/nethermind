// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Db;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Int256;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Logging;
using Nethermind.Evm.State;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Store.Test;

[TestFixture(false)]
[TestFixture(true)]
[Parallelizable(ParallelScope.All)]
public class StateProviderTests(bool useFlat)
{
    private static readonly Hash256 Hash1 = Keccak.Compute("1");
    private static readonly Hash256 Hash2 = Keccak.Compute("2");
    private readonly Address _address1 = new(Hash1);
    private static readonly ILogManager Logger = LimboLogs.Instance;

    private class Context : IDisposable
    {
        public IWorldState WorldState { get; }
        private readonly IContainer? _container;

        public Context(bool useFlat, ILogManager? logManager = null)
        {
            logManager ??= Logger;
            if (useFlat)
            {
                (IWorldStateScopeProvider scopeProvider, IContainer container) = TestWorldStateFactory.CreateFlatScopeProvider();
                _container = container;
                WorldState = new WorldState(scopeProvider, logManager);
            }
            else
            {
                WorldState = TestWorldStateFactory.CreateForTest(logManager: logManager);
            }
        }

        public void Dispose() => _container?.Dispose();
    }

    [Test]
    public void ApplyAccountOverlay_AfterBlockStartCommit_PreservesUnchangedFields()
    {
        using Context ctx = new(useFlat);
        IWorldState state = ctx.WorldState;
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        state.CreateAccount(_address1, 7, 3);
        state.Commit(Frontier.Instance);

        Assert.That(state.TryApplyAccountOverlay(new BalanceOverlay(_address1)), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.GetBalance(_address1), Is.EqualTo((UInt256)99), "the prefix replaces the cached balance");
            Assert.That(state.GetNonce(_address1), Is.EqualTo(3), "the block-start nonce is not flushed to the scope and must survive");
        }
    }

    [Test]
    public void ApplyAccountOverlay_WhenMissingAccountWasCached_UsesCreatedAccount()
    {
        using Context ctx = new(useFlat);
        IWorldState state = ctx.WorldState;
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        state.WarmUp(_address1);
        Assert.That(state.GetBalance(_address1), Is.EqualTo(UInt256.Zero));
        state.Commit(Frontier.Instance);

        Assert.That(state.TryApplyAccountOverlay(new BalanceOverlay(_address1)), Is.True);

        Assert.That(state.GetBalance(_address1), Is.EqualTo((UInt256)99), "cached misses must not hide the prefix-created account");
    }

    [Test]
    public void ApplyAccountOverlay_WhenBlockStartStorageOverlaps_RefusesWithoutChangingAccounts([Values] bool write)
    {
        using Context ctx = new(useFlat);
        IWorldState state = ctx.WorldState;
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        state.CreateAccount(_address1, 7, 3);
        StorageCell cell = new(_address1, UInt256.One);
        state.Get(cell, out _);
        if (write) state.Set(cell, (UInt256)42);
        state.Commit(Frontier.Instance);

        Assert.That(state.TryApplyAccountOverlay(new BalanceOverlay(_address1, hasStorage: true)), Is.False);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.GetBalance(_address1), Is.EqualTo((UInt256)7));
            state.Get(cell, out UInt256 value);
            Assert.That(value, Is.EqualTo(write ? (UInt256)42 : UInt256.Zero));
        }
    }

    [Test]
    public void Eip_158_zero_value_transfer_deletes()
    {
        using Context ctx = new(useFlat);
        IWorldState frontierProvider = ctx.WorldState;
        BlockHeader baseBlock;
        using (IDisposable _ = frontierProvider.BeginScope(IWorldState.PreGenesis))
        {
            frontierProvider.CreateAccount(_address1, 0);
            frontierProvider.Commit(Frontier.Instance);
            frontierProvider.CommitTree(0);
            baseBlock = Build.A.BlockHeader.WithStateRoot(frontierProvider.StateRoot).TestObject;
        }

        IWorldState provider = frontierProvider;
        using (IDisposable _ = provider.BeginScope(baseBlock))
        {
            provider.AddToBalance(_address1, 0, SpuriousDragon.Instance);
            provider.Commit(SpuriousDragon.Instance);
            Assert.That(provider.AccountExists(_address1), Is.False);
        }
    }

    [Test]
    public void Cold_account_read_tracks_writes_and_rollback([Values] bool exists)
    {
        using Context ctx = new(useFlat);
        IWorldState provider = ctx.WorldState;
        BlockHeader baseBlock;
        using (provider.BeginScope(IWorldState.PreGenesis))
        {
            if (exists) provider.CreateAccount(_address1, 1);
            provider.Commit(Frontier.Instance);
            provider.CommitTree(0);
            baseBlock = Build.A.BlockHeader.WithStateRoot(provider.StateRoot).TestObject;
        }

        using IDisposable scope = provider.BeginScope(baseBlock);
        Assert.That(provider.AccountExists(_address1), Is.EqualTo(exists));
        Snapshot snapshot = provider.TakeSnapshot();
        if (exists) provider.AddToBalance(_address1, 2, Frontier.Instance);
        else provider.CreateAccount(_address1, 3);
        Assert.That(provider.GetBalance(_address1), Is.EqualTo((UInt256)3));

        provider.Restore(snapshot);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.AccountExists(_address1), Is.EqualTo(exists));
            Assert.That(provider.GetBalance(_address1), Is.EqualTo(exists ? UInt256.One : UInt256.Zero));
        }
    }

    [Test]
    public void Eip_158_touch_zero_value_system_account_is_not_deleted()
    {
        using Context ctx = new(useFlat);
        IWorldState provider = ctx.WorldState;
        using IDisposable _ = provider.BeginScope(IWorldState.PreGenesis);
        Address systemUser = Address.SystemUser;

        provider.CreateAccount(systemUser, 0);
        provider.Commit(Homestead.Instance);

        ReleaseSpec releaseSpec = new() { IsEip158Enabled = true, Eip158IgnoredAccount = systemUser };
        provider.InsertCode(systemUser, System.Text.Encoding.UTF8.GetBytes(""), releaseSpec);
        provider.Commit(releaseSpec);

        Assert.That(provider.AccountExists(systemUser), Is.True);
    }

    [Test]
    public void Updating_code_hash_is_not_logged_at_debug()
    {
        TestLogger logger = new() { IsTrace = false };
        using Context ctx = new(useFlat, new OneLoggerLogManager(new ILogger(logger)));
        IWorldState provider = ctx.WorldState;
        using IDisposable _ = provider.BeginScope(IWorldState.PreGenesis);
        provider.CreateAccount(_address1, 0);
        logger.LogList.Clear();

        provider.InsertCode(_address1, new byte[] { 1 }, Frontier.Instance);

        Assert.That(logger.LogList, Has.None.Contains($"Update {_address1} C"));
    }

    [Test]
    public void Empty_commit_restore()
    {
        using Context ctx = new(useFlat);
        IWorldState provider = ctx.WorldState;
        using IDisposable _ = provider.BeginScope(IWorldState.PreGenesis);
        provider.Commit(Frontier.Instance);
        provider.Restore(Snapshot.Empty);
    }

    [Test]
    public void Update_balance_on_non_existing_account_throws()
    {
        using Context ctx = new(useFlat);
        IWorldState provider = ctx.WorldState;
        using IDisposable _ = provider.BeginScope(IWorldState.PreGenesis);
        Assert.Throws<InvalidOperationException>(() => provider.AddToBalance(TestItem.AddressA, 1.Ether, Olympic.Instance));
    }

    [Test]
    public void Is_empty_account()
    {
        using Context ctx = new(useFlat);
        IWorldState provider = ctx.WorldState;
        using IDisposable _ = provider.BeginScope(IWorldState.PreGenesis);
        provider.CreateAccount(_address1, 0);
        provider.Commit(Frontier.Instance);
        bool isEmpty = !provider.TryGetAccount(_address1, out AccountStruct account) || account.IsEmpty;
        Assert.That(isEmpty, Is.True);
    }

    [Test]
    public void Returns_empty_byte_code_for_non_existing_accounts()
    {
        using Context ctx = new(useFlat);
        IWorldState provider = ctx.WorldState;
        using IDisposable _ = provider.BeginScope(IWorldState.PreGenesis);
        byte[] code = provider.GetCode(TestItem.AddressA)!;
        Assert.That(code, Is.Empty);
    }

    [Test]
    public void Restore_update_restore()
    {
        using Context ctx = new(useFlat);
        IWorldState provider = ctx.WorldState;
        using IDisposable _ = provider.BeginScope(IWorldState.PreGenesis);
        provider.CreateAccount(_address1, 0);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.Restore(new Snapshot(Snapshot.Storage.Empty, 4));
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.Restore(new Snapshot(Snapshot.Storage.Empty, 4));
        Assert.That(provider.GetBalance(_address1), Is.EqualTo((UInt256)4));
    }

    [Test]
    public void Keep_in_cache()
    {
        using Context ctx = new(useFlat);
        IWorldState provider = ctx.WorldState;
        using IDisposable _ = provider.BeginScope(IWorldState.PreGenesis);
        provider.CreateAccount(_address1, 0);
        provider.Commit(Frontier.Instance);
        provider.GetBalance(_address1);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.Restore(Snapshot.Empty);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.Restore(Snapshot.Empty);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.Restore(Snapshot.Empty);
        Assert.That(provider.GetBalance(_address1), Is.EqualTo(UInt256.Zero));
    }

    [Test]
    public void Restore_in_the_middle()
    {
        byte[] code = [1];

        using Context ctx = new(useFlat);
        IWorldState provider = ctx.WorldState;
        using IDisposable _ = provider.BeginScope(IWorldState.PreGenesis);
        provider.CreateAccount(_address1, 1);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.IncrementNonce(_address1);
        provider.InsertCode(_address1, new byte[] { 1 }, Frontier.Instance, false);

        Assert.That(provider.GetNonce(_address1), Is.EqualTo(1UL));
        Assert.That(provider.GetBalance(_address1), Is.EqualTo(UInt256.One + 1));
        Assert.That(provider.GetCode(_address1), Is.EqualTo(code));
        provider.Restore(new Snapshot(Snapshot.Storage.Empty, 3));
        Assert.That(provider.GetNonce(_address1), Is.EqualTo(1UL));
        Assert.That(provider.GetBalance(_address1), Is.EqualTo(UInt256.One + 1));
        Assert.That(provider.GetCode(_address1), Is.EqualTo(code));
        provider.Restore(new Snapshot(Snapshot.Storage.Empty, 2));
        Assert.That(provider.GetNonce(_address1), Is.EqualTo(1UL));
        Assert.That(provider.GetBalance(_address1), Is.EqualTo(UInt256.One + 1));
        Assert.That(provider.GetCode(_address1), Is.EqualTo(Array.Empty<byte>()));
        provider.Restore(new Snapshot(Snapshot.Storage.Empty, 1));
        Assert.That(provider.GetNonce(_address1), Is.EqualTo(0UL));
        Assert.That(provider.GetBalance(_address1), Is.EqualTo(UInt256.One + 1));
        Assert.That(provider.GetCode(_address1), Is.EqualTo(Array.Empty<byte>()));
        provider.Restore(new Snapshot(Snapshot.Storage.Empty, 0));
        Assert.That(provider.GetNonce(_address1), Is.EqualTo(0UL));
        Assert.That(provider.GetBalance(_address1), Is.EqualTo(UInt256.One));
        Assert.That(provider.GetCode(_address1), Is.EqualTo(Array.Empty<byte>()));
        provider.Restore(new Snapshot(Snapshot.Storage.Empty, -1));
        Assert.That(provider.AccountExists(_address1), Is.EqualTo(false));
    }

    [Test(Description = "It was failing before as touch was marking the accounts as committed but not adding to trace list")]
    public void Touch_empty_trace_does_not_throw()
    {
        ParityLikeTxTracer tracer = new(Build.A.Block.TestObject, null, ParityTraceTypes.StateDiff);

        using Context ctx = new(useFlat);
        IWorldState provider = ctx.WorldState;
        using IDisposable _ = provider.BeginScope(IWorldState.PreGenesis);

        provider.CreateAccount(_address1, 0);
        provider.TryGetAccount(_address1, out AccountStruct account);

        Assert.That(account.IsEmpty, Is.True);
        provider.Commit(Frontier.Instance); // commit empty account (before the empty account fix in Spurious Dragon)
        Assert.That(provider.AccountExists(_address1), Is.True);

        provider.GetBalance(_address1); // justcache
        provider.AddToBalance(_address1, 0, SpuriousDragon.Instance); // touch
        Assert.DoesNotThrow(() => provider.Commit(SpuriousDragon.Instance, tracer));
    }

    [Test]
    public void Does_not_allow_calling_stateroot_after_scope()
    {
        using Context ctx = new(useFlat);
        IWorldState provider = ctx.WorldState;
        Action action = () => { _ = provider.StateRoot; };
        {
            using IDisposable _ = provider.BeginScope(IWorldState.PreGenesis);
            provider.CreateAccount(TestItem.AddressA, 5);
            provider.CommitTree(0);

            Assert.That(action, Throws.Nothing);
        }

        Assert.That(action, Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void InsertCode_after_scope_disposal_throws()
    {
        using Context ctx = new(useFlat);
        WorldState worldState = (WorldState)ctx.WorldState;
        IDisposable scope = worldState.BeginScope(IWorldState.PreGenesis);
        scope.Dispose();
        byte[] code = [0x00];
        ValueHash256 codeHash = ValueKeccak.Compute(code);

        Assert.That(
            () => worldState._stateProvider.InsertCode(_address1, codeHash, code, Frontier.Instance),
            Throws.TypeOf<InvalidOperationException>());
    }

    [TestCase(false, Description = "code of a reverted deployment is dropped")]
    [TestCase(true, Description = "code redeployed after the revert is still persisted")]
    public void Code_of_restored_deployment_is_persisted_only_when_redeployed(bool redeployAfterRestore)
    {
        using Context ctx = new(useFlat);
        IWorldState provider = ctx.WorldState;
        using IDisposable _ = provider.BeginScope(IWorldState.PreGenesis);

        IReleaseSpec spec = Prague.Instance;
        byte[] code = [0x60, 0x00, 0x60, 0x00, 0xf3];
        ValueHash256 codeHash = ValueKeccak.Compute(code);

        provider.CreateAccount(_address1, 1);
        provider.Commit(spec);

        Snapshot snapshot = provider.TakeSnapshot();

        // A successful child CREATE with a code deposit...
        provider.CreateAccount(TestItem.AddressB, 0);
        provider.InsertCode(TestItem.AddressB, code, spec);

        // ...undone by an ancestor REVERT.
        provider.Restore(snapshot);

        if (redeployAfterRestore)
        {
            provider.CreateAccount(TestItem.AddressC, 0);
            provider.InsertCode(TestItem.AddressC, code, spec);
        }

        provider.Commit(spec);

        Assert.That(provider.AccountExists(TestItem.AddressB), Is.False);
        if (redeployAfterRestore)
        {
            Assert.That(provider.GetCode(codeHash), Is.EqualTo(code));
        }
        else
        {
            Assert.That(() => provider.GetCode(codeHash), Throws.InstanceOf<InvalidOperationException>());
        }
    }

    [Test]
    public void Code_staged_before_a_snapshot_survives_a_restore_dropping_later_code()
    {
        using Context ctx = new(useFlat);
        IWorldState provider = ctx.WorldState;
        using IDisposable _ = provider.BeginScope(IWorldState.PreGenesis);

        IReleaseSpec spec = Prague.Instance;
        byte[] keptCode = [0x60, 0x00, 0x60, 0x00, 0xf3];
        byte[] revertedCode = [0x60, 0x01, 0x60, 0x00, 0xf3];
        ValueHash256 keptCodeHash = ValueKeccak.Compute(keptCode);
        ValueHash256 revertedCodeHash = ValueKeccak.Compute(revertedCode);

        provider.CreateAccount(TestItem.AddressB, 0);
        provider.InsertCode(TestItem.AddressB, keptCode, spec);

        Snapshot snapshot = provider.TakeSnapshot();

        provider.CreateAccount(TestItem.AddressC, 0);
        provider.InsertCode(TestItem.AddressC, revertedCode, spec);

        provider.Restore(snapshot);
        provider.Commit(spec);

        Assert.That(provider.GetCode(keptCodeHash), Is.EqualTo(keptCode));
        Assert.That(() => provider.GetCode(revertedCodeHash), Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void Code_committed_before_a_restore_is_still_persisted()
    {
        using Context ctx = new(useFlat);
        IWorldState provider = ctx.WorldState;
        using IDisposable _ = provider.BeginScope(IWorldState.PreGenesis);

        IReleaseSpec spec = Prague.Instance;
        byte[] code = [0x60, 0x00, 0x60, 0x00, 0xf3];
        ValueHash256 codeHash = ValueKeccak.Compute(code);

        provider.CreateAccount(TestItem.AddressB, 0);
        provider.InsertCode(TestItem.AddressB, code, spec);
        provider.Commit(spec, commitRoots: false);

        // A later transaction deploying the same code and reverting must not drop the
        // committed deployment's code.
        Snapshot snapshot = provider.TakeSnapshot();
        provider.CreateAccount(TestItem.AddressC, 0);
        provider.InsertCode(TestItem.AddressC, code, spec);
        provider.Restore(snapshot);

        provider.Commit(spec);

        Assert.That(provider.AccountExists(TestItem.AddressB), Is.True);
        Assert.That(provider.GetCode(codeHash), Is.EqualTo(code));
    }

    [Test]
    public void Code_committed_before_a_restore_is_still_persisted_when_the_insert_filter_evicted_it()
    {
        using Context ctx = new(useFlat);
        IWorldState provider = ctx.WorldState;
        using IDisposable _ = provider.BeginScope(IWorldState.PreGenesis);

        IReleaseSpec spec = Prague.Instance;
        byte[] code = [0x60, 0x00, 0x60, 0x00, 0xf3];
        ValueHash256 codeHash = ValueKeccak.Compute(code);

        provider.CreateAccount(TestItem.AddressB, 0);
        provider.InsertCode(TestItem.AddressB, code, spec);
        provider.Commit(spec, commitRoots: false);

        // Drop the committed entry from the insert filter, which is the only way the code batch
        // probe becomes reachable. Overflowing the filter instead would leave the coverage to its
        // 3-random eviction sampling, which can silently keep the entry.
        EvictFromCodeInsertFilter((WorldState)provider, codeHash);

        Snapshot snapshot = provider.TakeSnapshot();
        provider.CreateAccount(TestItem.AddressC, 0);
        bool reachedCodeBatch = provider.InsertCode(TestItem.AddressC, codeHash, code, spec);
        Assert.That(reachedCodeBatch, Is.True, "the insert filter short-circuited the re-insert");
        provider.Restore(snapshot);

        provider.Commit(spec);

        Assert.That(provider.AccountExists(TestItem.AddressB), Is.True);
        Assert.That(provider.GetCode(codeHash), Is.EqualTo(code));
    }

    [Test]
    public void Re_inserting_code_the_batch_already_holds_is_counted_once()
    {
        using Context ctx = new(useFlat);
        IWorldState provider = ctx.WorldState;
        using IDisposable _ = provider.BeginScope(IWorldState.PreGenesis);

        IReleaseSpec spec = Prague.Instance;
        byte[] code = [0x60, 0x00, 0x60, 0x00, 0xf3];
        ValueHash256 codeHash = ValueKeccak.Compute(code);

        provider.CreateAccount(TestItem.AddressB, 0);
        provider.InsertCode(TestItem.AddressB, code, spec);

        (long stagedWrites, long stagedBytes) = ReadCodeWriteCounters((WorldState)provider);
        Assert.That(stagedWrites, Is.EqualTo(1));
        Assert.That(stagedBytes, Is.EqualTo(code.Length));

        // Folds the counters into the globals and resets them, leaving the batch entry in place.
        provider.Commit(spec, commitRoots: false);
        EvictFromCodeInsertFilter((WorldState)provider, codeHash);

        provider.CreateAccount(TestItem.AddressC, 0);
        bool reachedCodeBatch = provider.InsertCode(TestItem.AddressC, codeHash, code, spec);
        Assert.That(reachedCodeBatch, Is.True, "the insert filter short-circuited the re-insert");

        // The batch already holds the only entry that reaches CodeDb, and no journal entry backs a
        // second count, so counting the re-insert would drift permanently.
        (long codeWrites, long codeBytesWritten) = ReadCodeWriteCounters((WorldState)provider);
        Assert.That(codeWrites, Is.Zero);
        Assert.That(codeBytesWritten, Is.Zero);
    }

    [Test]
    public void Code_re_staged_for_an_account_already_carrying_the_hash_is_rolled_back([Values] bool warmAccountBeforeSnapshot)
    {
        using Context ctx = new(useFlat);
        IWorldState provider = ctx.WorldState;
        using IDisposable _ = provider.BeginScope(IWorldState.PreGenesis);

        IReleaseSpec spec = Prague.Instance;
        byte[] code = [0x60, 0x00, 0x60, 0x00, 0xf3];
        ValueHash256 codeHash = ValueKeccak.Compute(code);

        provider.CreateAccount(TestItem.AddressB, 0);
        provider.InsertCode(TestItem.AddressB, code, spec);
        // Flushes the batch to CodeDb, leaving the account carrying the hash with nothing staged.
        provider.Commit(spec);

        EvictFromCodeInsertFilter((WorldState)provider, codeHash);
        EvictFromPersistedCodeHint((WorldState)provider, codeHash);

        // Warming the account first leaves the snapshot at the head of the change log, so the restore
        // below unwinds no account change at all and only the re-stage is left to drop.
        if (warmAccountBeforeSnapshot) provider.GetNonce(TestItem.AddressB);

        // Re-delegating an EIP-7702 authority to the target it already points at, with ContainsCode
        // only a hint: nothing suppresses the re-stage, and it pushes no code-hash change to anchor to.
        Snapshot snapshot = provider.TakeSnapshot();
        bool reachedCodeBatch = provider.InsertCode(TestItem.AddressB, codeHash, code, spec);
        Assert.That(reachedCodeBatch, Is.True, "the re-insert never reached the code batch");

        provider.Restore(snapshot);

        Assert.That(CodeBatchContains((WorldState)provider, codeHash), Is.False,
            "the re-stage outlived the changes it was made under");

        // Dropping the re-stage is only safe because the code is already durable, which is what lets
        // the account keep carrying its hash.
        Assert.That(provider.GetCode(codeHash), Is.EqualTo(code));

        (long codeWrites, long codeBytesWritten) = ReadCodeWriteCounters((WorldState)provider);
        Assert.That(codeWrites, Is.Zero);
        Assert.That(codeBytesWritten, Is.Zero);
    }

    [Test]
    public void Code_staged_by_discarded_changes_is_not_persisted()
    {
        using Context ctx = new(useFlat);
        IWorldState provider = ctx.WorldState;
        using IDisposable _ = provider.BeginScope(IWorldState.PreGenesis);

        IReleaseSpec spec = Prague.Instance;
        byte[] code = [0x60, 0x00, 0x60, 0x00, 0xf3];
        ValueHash256 codeHash = ValueKeccak.Compute(code);

        provider.CreateAccount(TestItem.AddressB, 0);
        provider.InsertCode(TestItem.AddressB, code, spec);

        // Drops the uncommitted changes while keeping block-level state, as CallAndRestore does.
        provider.Reset(resetBlockChanges: false);

        // The reset is the second RestoreCodeInserts caller, so it rolls the counters back too.
        (long codeWrites, long codeBytesWritten) = ReadCodeWriteCounters((WorldState)provider);
        Assert.That(codeWrites, Is.Zero);
        Assert.That(codeBytesWritten, Is.Zero);

        provider.Commit(spec);

        Assert.That(provider.AccountExists(TestItem.AddressB), Is.False);
        Assert.That(() => provider.GetCode(codeHash), Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void Same_code_can_be_redeployed_across_overlay_resets()
    {
        IContainer? containerToDispose = null;
        IWorldStateManager manager;
        if (useFlat)
        {
            (_, IContainer container) = TestWorldStateFactory.CreateFlatScopeProvider();
            containerToDispose = container;
            manager = container.Resolve<IWorldStateManager>();
        }
        else
        {
            IDbProvider dbProvider = TestMemDbProvider.Init();
            manager = TestWorldStateFactory.CreateWorldStateManagerForTest(dbProvider, LimboLogs.Instance);
        }

        try
        {
            using IOverridableWorldScope overridableScope = manager.CreateOverridableWorldScope();
            IWorldState worldState = new WorldState(overridableScope.WorldState, LimboLogs.Instance);

            byte[] code = [0x60, 0x60, 0x60, 0x40, 0x52, 0x00];
            Address addr = TestItem.AddressA;
            IReleaseSpec spec = Prague.Instance;

            // First scope — deploy + commit. Commit triggers CommitCodeAsync which, before
            // the fix, marked the shared filter on StateProvider as "persisted".
            using (worldState.BeginScope(IWorldState.PreGenesis))
            {
                worldState.CreateAccount(addr, 0);
                worldState.InsertCode(addr, code, spec);
                worldState.Commit(spec);

                Assert.That(worldState.GetCode(addr), Is.EqualTo(code));
            }

            // End of scope #1 — overlay's temp KV is discarded.
            overridableScope.ResetOverrides();

            // Second scope — same hash, fresh overlay. Before the fix, InsertCode consulted
            // the stale "persisted" filter, skipped the _codeBatch write, and the next
            // GetCode threw "Code 0x… is missing from the database".
            using (worldState.BeginScope(IWorldState.PreGenesis))
            {
                worldState.CreateAccount(addr, 0);
                worldState.InsertCode(addr, code, spec);

                Action getCode = () => worldState.GetCode(addr);
                Assert.That(getCode, Throws.Nothing);
                Assert.That(worldState.GetCode(addr), Is.EqualTo(code));
            }
        }
        finally
        {
            containerToDispose?.Dispose();
        }
    }

    /// <summary>
    /// Drops the code db's "already persisted" hint for a code hash, as a node restart would.
    /// No-op for code dbs that keep no hint, which already report <c>ContainsCode</c> false.
    /// </summary>
    private static void EvictFromPersistedCodeHint(WorldState worldState, in ValueHash256 codeHash)
    {
        object? codeDb = typeof(StateProvider)
            .GetField("_codeDb", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(worldState._stateProvider);
        AssociativeKeyCache<ValueHash256>? hint = codeDb?.GetType()
            .GetField("_persistedHint", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(codeDb) as AssociativeKeyCache<ValueHash256>;
        hint?.Delete(codeHash);
    }

    /// <summary>Reports whether code staged for CodeDb is still pending in the batch.</summary>
    private static bool CodeBatchContains(WorldState worldState, in ValueHash256 codeHash)
    {
        Dictionary<Hash256AsKey, byte[]>? batch = (Dictionary<Hash256AsKey, byte[]>?)typeof(StateProvider)
            .GetField("_codeBatch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(worldState._stateProvider);
        return batch?.ContainsKey(new Hash256(codeHash)) ?? false;
    }

    /// <summary>Reads the scope's un-flushed code-write counters.</summary>
    private static (long CodeWrites, long CodeBytesWritten) ReadCodeWriteCounters(WorldState worldState)
    {
        LocalMetrics metrics = (LocalMetrics)typeof(WorldState)
            .GetField("_localMetrics", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(worldState)!;
        return (metrics.CodeWrites, metrics.CodeBytesWritten);
    }

    /// <summary>Removes a code hash from <c>StateProvider</c>'s insert filter, as a capacity eviction would.</summary>
    private static void EvictFromCodeInsertFilter(WorldState worldState, in ValueHash256 codeHash)
    {
        AssociativeKeyCache<ValueHash256> filter = (AssociativeKeyCache<ValueHash256>)typeof(StateProvider)
            .GetField("_blockCodeInsertFilter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(worldState._stateProvider)!;
        Assert.That(filter.Delete(codeHash), Is.True, "the code hash was not in the insert filter");
    }

    private sealed class BalanceOverlay(Address address, bool hasStorage = false) : IStateReadOverlay
    {
        public bool TryGetAccount(Address candidate, Account? underlying, out Account? overlaid)
        {
            overlaid = (underlying ?? Account.TotallyEmpty).WithChangedBalance(99);
            return candidate == address;
        }

        public bool TryGetStorage(Address candidate, in UInt256 index, out UInt256 value)
        {
            value = default;
            return false;
        }

        public bool HasStorage(Address candidate) => hasStorage && candidate == address;
    }
}

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class CodeDbTests
{
    [TestCase(true, true)]
    [TestCase(false, false)]
    public void KeyValueWithBatchingBackedCodeDb_ContainsCode_respects_isPersistent_flag(bool isPersistent, bool expectedContains)
    {
        IKeyValueStoreWithBatching backing = new MemDb();
        TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb codeDb = new(backing, isPersistent);
        ValueHash256 hash = Keccak.Compute("any code").ValueHash256;

        codeDb.MarkCodePersisted(hash);

        Assert.That(codeDb.ContainsCode(hash), Is.EqualTo(expectedContains));
    }
}
