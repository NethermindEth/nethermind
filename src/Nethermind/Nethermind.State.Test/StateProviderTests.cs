// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using Autofac;
using Nethermind.Core;
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
using Nethermind.Evm.Tracing.State;
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

        public Context(bool useFlat)
        {
            if (useFlat)
            {
                (IWorldStateScopeProvider scopeProvider, IContainer container) = TestWorldStateFactory.CreateFlatScopeProvider();
                _container = container;
                WorldState = new WorldState(scopeProvider, Logger);
            }
            else
            {
                WorldState = TestWorldStateFactory.CreateForTest();
            }
        }

        public void Dispose() => _container?.Dispose();
    }

    private sealed class RecordingWorldStateTracer : IWorldStateTracer
    {
        public bool IsTracingState => true;
        public bool IsTracingStorage => false;
        public List<Address> AccountReads { get; } = [];

        public void ReportBalanceChange(Address address, UInt256? before, UInt256? after) { }

        public void ReportCodeChange(Address address, byte[]? before, byte[]? after) { }

        public void ReportNonceChange(Address address, UInt256? before, UInt256? after) { }

        public void ReportAccountRead(Address address) => AccountReads.Add(address);

        public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value) { }

        public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after) { }

        public void ReportStorageRead(in StorageCell storageCell) { }
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

        Assert.That(provider.GetNonce(_address1), Is.EqualTo(UInt256.One));
        Assert.That(provider.GetBalance(_address1), Is.EqualTo(UInt256.One + 1));
        Assert.That(provider.GetCode(_address1), Is.EqualTo(code));
        provider.Restore(new Snapshot(Snapshot.Storage.Empty, 3));
        Assert.That(provider.GetNonce(_address1), Is.EqualTo(UInt256.One));
        Assert.That(provider.GetBalance(_address1), Is.EqualTo(UInt256.One + 1));
        Assert.That(provider.GetCode(_address1), Is.EqualTo(code));
        provider.Restore(new Snapshot(Snapshot.Storage.Empty, 2));
        Assert.That(provider.GetNonce(_address1), Is.EqualTo(UInt256.One));
        Assert.That(provider.GetBalance(_address1), Is.EqualTo(UInt256.One + 1));
        Assert.That(provider.GetCode(_address1), Is.EqualTo(Array.Empty<byte>()));
        provider.Restore(new Snapshot(Snapshot.Storage.Empty, 1));
        Assert.That(provider.GetNonce(_address1), Is.EqualTo(UInt256.Zero));
        Assert.That(provider.GetBalance(_address1), Is.EqualTo(UInt256.One + 1));
        Assert.That(provider.GetCode(_address1), Is.EqualTo(Array.Empty<byte>()));
        provider.Restore(new Snapshot(Snapshot.Storage.Empty, 0));
        Assert.That(provider.GetNonce(_address1), Is.EqualTo(UInt256.Zero));
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
    public void Account_read_tracking_setting_returns_previous_value()
    {
        using Context ctx = new(useFlat);
        IWorldState provider = ctx.WorldState;

        Assert.That(provider.SetAccountReadTracking(false), Is.True);
        Assert.That(provider.SetAccountReadTracking(true), Is.False);
        Assert.That(provider.SetAccountReadTracking(true), Is.True);
    }

    [Test]
    public void Account_read_tracking_disabled_preserves_updates_and_restore()
    {
        using Context ctx = new(useFlat);
        IWorldState provider = ctx.WorldState;
        using IDisposable _ = provider.BeginScope(IWorldState.PreGenesis);

        provider.CreateAccount(_address1, 10);
        provider.Commit(Frontier.Instance);

        Assert.That(provider.SetAccountReadTracking(false), Is.True);
        Snapshot snapshot = provider.TakeSnapshot();

        provider.GetBalance(_address1);
        provider.AddToBalance(_address1, 5, SpuriousDragon.Instance);

        Assert.That(provider.GetBalance(_address1), Is.EqualTo((UInt256)15));

        provider.Restore(snapshot);

        Assert.That(provider.GetBalance(_address1), Is.EqualTo((UInt256)10));

        provider.SetAccountReadTracking(true);
        provider.AddToBalance(_address1, 2, SpuriousDragon.Instance);
        provider.Commit(SpuriousDragon.Instance);

        Assert.That(provider.GetBalance(_address1), Is.EqualTo((UInt256)12));
    }

    [Test]
    public void Account_read_tracking_enabled_reports_read_only_account()
    {
        using Context ctx = new(useFlat);
        IWorldState provider = ctx.WorldState;
        using IDisposable _ = provider.BeginScope(IWorldState.PreGenesis);
        RecordingWorldStateTracer tracer = new();

        provider.CreateAccount(_address1, 10);
        provider.Commit(Frontier.Instance);

        provider.GetBalance(_address1);
        provider.Commit(SpuriousDragon.Instance, tracer);

        Assert.That(tracer.AccountReads, Contains.Item(_address1));
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
