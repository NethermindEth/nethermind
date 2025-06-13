// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Store.Test;

[Parallelizable(ParallelScope.All)]
public class StateProviderTests
{
    private static readonly Hash256 Hash1 = Keccak.Compute("1");
    private static readonly Hash256 Hash2 = Keccak.Compute("2");
    private readonly Address _address1 = new(Hash1);
    private static readonly ILogManager Logger = LimboLogs.Instance;

    [Test]
    public void Eip_158_zero_value_transfer_deletes()
    {
        WorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest();
        IWorldState frontierProvider = worldStateManager.GlobalWorldState;
        frontierProvider.CreateAccount(_address1, 0);
        frontierProvider.Commit(Frontier.Instance);
        frontierProvider.CommitTree(0);

        IWorldState provider = worldStateManager.GlobalWorldState;
        provider.StateRoot = frontierProvider.StateRoot;

        provider.AddToBalance(_address1, 0, SpuriousDragon.Instance);
        provider.Commit(SpuriousDragon.Instance);
        Assert.That(provider.AccountExists(_address1), Is.False);
    }

    [Test]
    public void Eip_158_touch_zero_value_system_account_is_not_deleted()
    {
        WorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest();
        IWorldState provider = worldStateManager.GlobalWorldState;
        var systemUser = Address.SystemUser;

        provider.CreateAccount(systemUser, 0);
        provider.Commit(Homestead.Instance);

        var releaseSpec = new ReleaseSpec() { IsEip158Enabled = true };
        provider.InsertCode(systemUser, System.Text.Encoding.UTF8.GetBytes(""), releaseSpec);
        provider.Commit(releaseSpec);

        ((WorldState)provider).GetAccount(systemUser).Should().NotBeNull();
    }

    [Test]
    public void Can_dump_state()
    {
        WorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest();
        IWorldState provider = worldStateManager.GlobalWorldState;
        provider.CreateAccount(TestItem.AddressA, 1.Ether());
        provider.Commit(MuirGlacier.Instance);
        provider.CommitTree(0);

        string state = provider.DumpState();
        state.Should().NotBeEmpty();
    }

    [Test]
    public void Can_accepts_visitors()
    {
        WorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest();
        IWorldState provider = worldStateManager.GlobalWorldState;
        provider.CreateAccount(TestItem.AddressA, 1.Ether());
        provider.Commit(MuirGlacier.Instance);
        provider.CommitTree(0);

        TrieStatsCollector visitor = new(new MemDb(), LimboLogs.Instance);
        provider.Accept(visitor, provider.StateRoot);
    }

    [Test]
    public void Empty_commit_restore()
    {
        WorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest();
        IWorldState provider = worldStateManager.GlobalWorldState;
        provider.Commit(Frontier.Instance);
        provider.Restore(Snapshot.Empty);
    }

    [Test]
    public void Update_balance_on_non_existing_account_throws()
    {
        WorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest();
        IWorldState provider = worldStateManager.GlobalWorldState;
        Assert.Throws<InvalidOperationException>(() => provider.AddToBalance(TestItem.AddressA, 1.Ether(), Olympic.Instance));
    }

    [Test]
    public void Is_empty_account()
    {
        WorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest();
        IWorldState provider = worldStateManager.GlobalWorldState;
        provider.CreateAccount(_address1, 0);
        provider.Commit(Frontier.Instance);
        Assert.That(provider.IsEmptyAccount(_address1), Is.True);
    }

    [Test]
    public void Returns_empty_byte_code_for_non_existing_accounts()
    {
        WorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest();
        IWorldState provider = worldStateManager.GlobalWorldState;
        byte[] code = provider.GetCode(TestItem.AddressA);
        code.Should().BeEmpty();
    }

    [Test]
    public void Restore_update_restore()
    {
        WorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest();
        IWorldState provider = worldStateManager.GlobalWorldState;
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
        WorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest();
        IWorldState provider = worldStateManager.GlobalWorldState;
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

        WorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest();
        IWorldState provider = worldStateManager.GlobalWorldState;
        provider.CreateAccount(_address1, 1);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.IncrementNonce(_address1);
        provider.InsertCode(_address1, new byte[] { 1 }, Frontier.Instance, false);
        provider.UpdateStorageRoot(_address1, Hash2);

        Assert.That(provider.GetNonce(_address1), Is.EqualTo(UInt256.One));
        Assert.That(provider.GetBalance(_address1), Is.EqualTo(UInt256.One + 1));
        Assert.That(provider.GetCode(_address1), Is.EqualTo(code));
        provider.Restore(new Snapshot(Snapshot.Storage.Empty, 4));
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

        WorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest();
        IWorldState provider = worldStateManager.GlobalWorldState;
        provider.CreateAccount(_address1, 0);
        provider.TryGetAccount(_address1, out AccountStruct account);

        Assert.That(account.IsEmpty, Is.True);
        provider.Commit(Frontier.Instance); // commit empty account (before the empty account fix in Spurious Dragon)
        Assert.That(provider.AccountExists(_address1), Is.True);

        provider.Reset(); // clear all caches

        provider.GetBalance(_address1); // justcache
        provider.AddToBalance(_address1, 0, SpuriousDragon.Instance); // touch
        Assert.DoesNotThrow(() => provider.Commit(SpuriousDragon.Instance, tracer));
    }

    [Test]
    public void Does_not_require_recalculation_after_reset()
    {
        WorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest();
        IWorldState provider = worldStateManager.GlobalWorldState;
        provider.CreateAccount(TestItem.AddressA, 5);

        Action action = () => { _ = provider.StateRoot; };
        action.Should().Throw<InvalidOperationException>();

        provider.Reset();
        action.Should().NotThrow<InvalidOperationException>();
    }
}
