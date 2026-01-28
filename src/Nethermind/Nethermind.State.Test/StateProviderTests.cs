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
using Nethermind.Int256;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Evm.Tracing.State;
using Nethermind.Logging;
using Nethermind.Evm.State;
using Nethermind.State;
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
        IWorldState frontierProvider = TestWorldStateFactory.CreateForTest();
        BlockHeader baseBlock;
        using (var _ = frontierProvider.BeginScope(IWorldState.PreGenesis))
        {
            frontierProvider.CreateAccount(_address1, 0);
            frontierProvider.Commit(Frontier.Instance);
            frontierProvider.CommitTree(0);
            baseBlock = Build.A.BlockHeader.WithStateRoot(frontierProvider.StateRoot).TestObject;
        }

        IWorldState provider = frontierProvider;
        using (var _ = provider.BeginScope(baseBlock))
        {
            provider.AddToBalance(_address1, 0, SpuriousDragon.Instance);
            provider.Commit(SpuriousDragon.Instance);
            Assert.That(provider.AccountExists(_address1), Is.False);
        }
    }

    [Test]
    public void Eip_158_touch_zero_value_system_account_is_not_deleted()
    {
        IWorldState provider = TestWorldStateFactory.CreateForTest();
        using var _ = provider.BeginScope(IWorldState.PreGenesis);
        var systemUser = Address.SystemUser;

        provider.CreateAccount(systemUser, 0);
        provider.Commit(Homestead.Instance);

        var releaseSpec = new ReleaseSpec() { IsEip158Enabled = true };
        provider.InsertCode(systemUser, System.Text.Encoding.UTF8.GetBytes(""), releaseSpec);
        provider.Commit(releaseSpec);

        provider.GetAccount(systemUser).Should().NotBeNull();
    }

    [Test]
    public void Empty_commit_restore()
    {
        IWorldState provider = TestWorldStateFactory.CreateForTest();
        using var _ = provider.BeginScope(IWorldState.PreGenesis);
        provider.Commit(Frontier.Instance);
        provider.Restore(Snapshot.Empty);
    }

    [Test]
    public void Update_balance_on_non_existing_account_throws()
    {
        IWorldState provider = TestWorldStateFactory.CreateForTest();
        using var _ = provider.BeginScope(IWorldState.PreGenesis);
        Assert.Throws<InvalidOperationException>(() => provider.AddToBalance(TestItem.AddressA, 1.Ether(), Olympic.Instance));
    }

    [Test]
    public void Is_empty_account()
    {
        IWorldState provider = TestWorldStateFactory.CreateForTest();
        using var _ = provider.BeginScope(IWorldState.PreGenesis);
        provider.CreateAccount(_address1, 0);
        provider.Commit(Frontier.Instance);
        bool isEmpty = !provider.TryGetAccount(_address1, out var account) || account.IsEmpty;
        isEmpty.Should().BeTrue();
    }

    [Test]
    public void Returns_empty_byte_code_for_non_existing_accounts()
    {
        IWorldState provider = TestWorldStateFactory.CreateForTest();
        using var _ = provider.BeginScope(IWorldState.PreGenesis);
        byte[] code = provider.GetCode(TestItem.AddressA);
        code.Should().BeEmpty();
    }

    [Test]
    public void Restore_update_restore()
    {
        IWorldState provider = TestWorldStateFactory.CreateForTest();
        using var _ = provider.BeginScope(IWorldState.PreGenesis);
        provider.CreateAccount(_address1, 0);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        Snapshot snapshot = provider.TakeSnapshot();
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.Restore(snapshot);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.Restore(snapshot);
        Assert.That(provider.GetBalance(_address1), Is.EqualTo((UInt256)4));
    }

    [Test]
    public void Keep_in_cache()
    {
        IWorldState provider = TestWorldStateFactory.CreateForTest();
        using var _ = provider.BeginScope(IWorldState.PreGenesis);
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
    public void Restore_same_token_after_noop_restore_undoes_new_mutations()
    {
        IWorldState provider = TestWorldStateFactory.CreateForTest();
        using var _ = provider.BeginScope(IWorldState.PreGenesis);

        provider.CreateAccount(_address1, 0);
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        Snapshot snapshot = provider.TakeSnapshot();

        // No writes after snapshot: this restore trims empty frames only.
        provider.Restore(snapshot);

        provider.AddToBalance(_address1, 1, Frontier.Instance);
        provider.Restore(snapshot);

        provider.GetBalance(_address1).Should().Be((UInt256)1);
    }

    [Test]
    public void Restore_in_the_middle()
    {
        byte[] code = [1];

        IWorldState provider = TestWorldStateFactory.CreateForTest();
        using var _ = provider.BeginScope(IWorldState.PreGenesis);
        provider.CreateAccount(_address1, 1);
        Snapshot snapshotAfterCreate = provider.TakeSnapshot();
        provider.AddToBalance(_address1, 1, Frontier.Instance);
        Snapshot snapshotAfterBalance = provider.TakeSnapshot();
        provider.IncrementNonce(_address1);
        Snapshot snapshotAfterNonce = provider.TakeSnapshot();
        provider.InsertCode(_address1, new byte[] { 1 }, Frontier.Instance, false);
        Snapshot snapshotAfterCode = provider.TakeSnapshot();

        Assert.That(provider.GetNonce(_address1), Is.EqualTo(UInt256.One));
        Assert.That(provider.GetBalance(_address1), Is.EqualTo(UInt256.One + 1));
        Assert.That(provider.GetCode(_address1), Is.EqualTo(code));
        provider.Restore(snapshotAfterCode);
        Assert.That(provider.GetNonce(_address1), Is.EqualTo(UInt256.One));
        Assert.That(provider.GetBalance(_address1), Is.EqualTo(UInt256.One + 1));
        Assert.That(provider.GetCode(_address1), Is.EqualTo(code));
        provider.Restore(snapshotAfterNonce);
        Assert.That(provider.GetNonce(_address1), Is.EqualTo(UInt256.One));
        Assert.That(provider.GetBalance(_address1), Is.EqualTo(UInt256.One + 1));
        Assert.That(provider.GetCode(_address1), Is.EqualTo(Array.Empty<byte>()));
        provider.Restore(snapshotAfterBalance);
        Assert.That(provider.GetNonce(_address1), Is.EqualTo(UInt256.Zero));
        Assert.That(provider.GetBalance(_address1), Is.EqualTo(UInt256.One + 1));
        Assert.That(provider.GetCode(_address1), Is.EqualTo(Array.Empty<byte>()));
        provider.Restore(snapshotAfterCreate);
        Assert.That(provider.GetNonce(_address1), Is.EqualTo(UInt256.Zero));
        Assert.That(provider.GetBalance(_address1), Is.EqualTo(UInt256.One));
        Assert.That(provider.GetCode(_address1), Is.EqualTo(Array.Empty<byte>()));
        provider.Restore(new Snapshot(Snapshot.Storage.Empty, -1));
        Assert.That(provider.AccountExists(_address1), Is.EqualTo(false));
    }

    [Test]
    public void Nested_balance_nonce_and_code_restore_correctly()
    {
        byte[] code = [1];
        IWorldState provider = TestWorldStateFactory.CreateForTest();
        using var _ = provider.BeginScope(IWorldState.PreGenesis);

        provider.CreateAccount(_address1, 10);
        provider.InsertCode(_address1, code, Frontier.Instance);
        Snapshot outerSnapshot = provider.TakeSnapshot();

        provider.AddToBalance(_address1, 5, Frontier.Instance);
        provider.IncrementNonce(_address1);

        Snapshot innerSnapshot = provider.TakeSnapshot();
        provider.AddToBalance(_address1, 7, Frontier.Instance);
        provider.IncrementNonce(_address1);

        provider.Restore(innerSnapshot);
        provider.GetBalance(_address1).Should().Be((UInt256)15);
        provider.GetNonce(_address1).Should().Be(UInt256.One);
        provider.GetCode(_address1).Should().Equal(code);

        provider.Restore(outerSnapshot);
        provider.GetBalance(_address1).Should().Be((UInt256)10);
        provider.GetNonce(_address1).Should().Be(UInt256.Zero);
        provider.GetCode(_address1).Should().Equal(code);
    }

    [Test]
    public void Add_to_balance_and_increment_nonce_restores_as_single_mutation()
    {
        IWorldState provider = TestWorldStateFactory.CreateForTest();
        using var _ = provider.BeginScope(IWorldState.PreGenesis);

        provider.CreateAccount(_address1, 1);
        Snapshot snapshot = provider.TakeSnapshot();

        provider.AddToBalanceAndCreateIfNotExists(_address1, 5, Frontier.Instance, incrementNonce: true).Should().BeFalse();
        provider.GetBalance(_address1).Should().Be((UInt256)6);
        provider.GetNonce(_address1).Should().Be(UInt256.One);

        provider.Restore(snapshot);
        provider.GetBalance(_address1).Should().Be((UInt256)1);
        provider.GetNonce(_address1).Should().Be(UInt256.Zero);
    }

    [Test(Description = "It was failing before as touch was marking the accounts as committed but not adding to trace list")]
    public void Touch_empty_trace_does_not_throw()
    {
        ParityLikeTxTracer tracer = new(Build.A.Block.TestObject, null, ParityTraceTypes.StateDiff);

        IWorldState provider = TestWorldStateFactory.CreateForTest();

        using var _ = provider.BeginScope(IWorldState.PreGenesis);

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
    public void Commit_retainInCache_preserves_written_sender()
    {
        IWorldState provider = TestWorldStateFactory.CreateForTest();
        using var _ = provider.BeginScope(IWorldState.PreGenesis);

        Address sender = TestItem.AddressA;
        provider.CreateAccount(sender, 1.Ether());
        provider.Commit(London.Instance);
        provider.CommitTree(0);

        // Simulate BuyGas + IncrementNonce modifying sender before Commit#1
        provider.SubtractFromBalance(sender, 21000, London.Instance);
        provider.IncrementNonce(sender);
        provider.Commit(London.Instance, NullStateTracer.Instance, commitRoots: false, retainInCache: sender);

        // After Commit#1 with retain, sender should still be readable with correct state
        provider.GetBalance(sender).Should().Be(1.Ether() - 21000);
        provider.GetAccount(sender).Nonce.Should().Be(UInt256.One);
    }

    [Test]
    public void Commit_retainInCache_preserves_read_only_sender()
    {
        IWorldState provider = TestWorldStateFactory.CreateForTest();
        using var _ = provider.BeginScope(IWorldState.PreGenesis);

        Address sender = TestItem.AddressA;
        provider.CreateAccount(sender, 1.Ether());
        provider.Commit(London.Instance);
        provider.CommitTree(0);

        // Simulate SystemTransactionProcessor: sender only read, BuyGas/IncrementNonce are no-ops
        provider.AccountExists(sender); // read-only: creates JustCache entry, no _blockChanges write
        provider.Commit(London.Instance, NullStateTracer.Instance, commitRoots: false, retainInCache: sender);

        // Sender must still be readable with correct account state after retain
        provider.AccountExists(sender).Should().BeTrue();
        provider.GetBalance(sender).Should().Be(1.Ether());
    }

    [Test]
    public void Multi_address_frame_revert_restores_all()
    {
        IWorldState provider = TestWorldStateFactory.CreateForTest();
        using var _ = provider.BeginScope(IWorldState.PreGenesis);

        Address a = TestItem.AddressA;
        Address b = TestItem.AddressB;
        Address c = TestItem.AddressC;
        provider.CreateAccount(a, 10);
        provider.CreateAccount(b, 20);
        provider.CreateAccount(c, 30);

        Snapshot snapshot = provider.TakeSnapshot();

        provider.AddToBalance(a, 1, Frontier.Instance);
        provider.AddToBalance(b, 2, Frontier.Instance);
        provider.AddToBalance(c, 3, Frontier.Instance);

        provider.GetBalance(a).Should().Be((UInt256)11);
        provider.GetBalance(b).Should().Be((UInt256)22);
        provider.GetBalance(c).Should().Be((UInt256)33);

        provider.Restore(snapshot);

        provider.GetBalance(a).Should().Be((UInt256)10);
        provider.GetBalance(b).Should().Be((UInt256)20);
        provider.GetBalance(c).Should().Be((UInt256)30);
    }

    [Test]
    public void Delete_revert_restores_account()
    {
        IWorldState provider = TestWorldStateFactory.CreateForTest();
        using var _ = provider.BeginScope(IWorldState.PreGenesis);

        provider.CreateAccount(_address1, 42);
        Snapshot snapshot = provider.TakeSnapshot();

        provider.DeleteAccount(_address1);
        provider.AccountExists(_address1).Should().BeFalse();

        provider.Restore(snapshot);
        provider.AccountExists(_address1).Should().BeTrue();
        provider.GetBalance(_address1).Should().Be((UInt256)42);
    }

    [Test]
    public void Create_revert_removes_account()
    {
        IWorldState provider = TestWorldStateFactory.CreateForTest();
        using var _ = provider.BeginScope(IWorldState.PreGenesis);

        Snapshot snapshot = provider.TakeSnapshot();
        provider.CreateAccount(_address1, 100);
        provider.AccountExists(_address1).Should().BeTrue();

        provider.Restore(snapshot);
        provider.AccountExists(_address1).Should().BeFalse();
    }

    [Test]
    public void Commit_after_revert_writes_pre_revert_state()
    {
        IWorldState provider = TestWorldStateFactory.CreateForTest();
        using var _ = provider.BeginScope(IWorldState.PreGenesis);

        provider.CreateAccount(_address1, 10);
        Snapshot snapshot = provider.TakeSnapshot();
        provider.AddToBalance(_address1, 90, Frontier.Instance);

        // Balance is 100 before revert
        provider.GetBalance(_address1).Should().Be((UInt256)100);

        provider.Restore(snapshot);
        // Balance should be back to 10
        provider.GetBalance(_address1).Should().Be((UInt256)10);

        // Commit the reverted state — blockChanges should see balance=10
        provider.Commit(Frontier.Instance);
        provider.CommitTree(0);

        // Re-read from trie to verify committed state
        Hash256 root = provider.StateRoot;
        root.Should().NotBe(Keccak.EmptyTreeHash);
        provider.GetBalance(_address1).Should().Be((UInt256)10);
    }

    [Test]
    public void Sequential_transactions_accumulate_state()
    {
        IWorldState provider = TestWorldStateFactory.CreateForTest();
        using var _ = provider.BeginScope(IWorldState.PreGenesis);

        Address a = TestItem.AddressA;
        Address b = TestItem.AddressB;

        // TX1: create a with balance 100
        provider.CreateAccount(a, 100);
        provider.Commit(Frontier.Instance);

        // TX2: create b, modify a
        provider.CreateAccount(b, 200);
        provider.AddToBalance(a, 50, Frontier.Instance);
        provider.Commit(Frontier.Instance);

        // TX3: modify both
        provider.SubtractFromBalance(a, 30, Frontier.Instance);
        provider.AddToBalance(b, 70, Frontier.Instance);
        provider.Commit(Frontier.Instance);

        // Verify accumulated state after three sequential commits
        provider.GetBalance(a).Should().Be((UInt256)120); // 100 + 50 - 30
        provider.GetBalance(b).Should().Be((UInt256)270); // 200 + 70
    }

    [Test]
    public void Does_not_allow_calling_stateroot_after_scope()
    {
        IWorldState provider = TestWorldStateFactory.CreateForTest();

        Action action = () => { _ = provider.StateRoot; };
        {
            using var _ = provider.BeginScope(IWorldState.PreGenesis);
            provider.CreateAccount(TestItem.AddressA, 5);
            provider.CommitTree(0);

            action.Should().NotThrow<InvalidOperationException>();
        }

        action.Should().Throw<InvalidOperationException>();
    }
}
