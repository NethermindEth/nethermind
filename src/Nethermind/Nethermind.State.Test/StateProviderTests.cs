// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Paprika;
using System.Threading.Tasks;

namespace Nethermind.Store.Test
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class StateProviderTests
    {
        private static readonly Hash256 Hash1 = Keccak.Compute("1");
        private static readonly Hash256 Hash2 = Keccak.Compute("2");
        private readonly Address _address1 = new(Hash1);
        private static readonly ILogManager Logger = LimboLogs.Instance;
        private IDb _codeDb;

        [SetUp]
        public void Setup()
        {
            _codeDb = new MemDb();
        }

        [TearDown]
        public void TearDown() => _codeDb?.Dispose();

        [Test]
        public async Task Eip_158_zero_value_transfer_deletes()
        {
            await using PaprikaStateFactory stateDb = new();
            WorldState frontierProvider = new(stateDb, _codeDb, Logger);
            frontierProvider.CreateAccount(_address1, 0);
            frontierProvider.Commit(Frontier.Instance);
            frontierProvider.CommitTree(0);

            WorldState provider = new(stateDb, _codeDb, Logger);
            provider.StateRoot = frontierProvider.StateRoot;

            provider.AddToBalance(_address1, 0, SpuriousDragon.Instance);
            provider.Commit(SpuriousDragon.Instance);
            Assert.False(provider.AccountExists(_address1));

            _codeDb = Substitute.For<IDb>();
        }

        [Test]
        public async Task Eip_158_touch_zero_value_system_account_is_not_deleted()
        {
            await using PaprikaStateFactory stateDb = new();
            WorldState provider = new(stateDb, _codeDb, Logger);
            var systemUser = Address.SystemUser;

            provider.CreateAccount(systemUser, 0);
            provider.Commit(Homestead.Instance);

            var releaseSpec = new ReleaseSpec() { IsEip158Enabled = true };
            provider.InsertCode(systemUser, System.Text.Encoding.UTF8.GetBytes(""), releaseSpec);
            provider.Commit(releaseSpec);

            provider.GetAccount(systemUser).Should().NotBeNull();
        }

        [Test]
        public async Task Can_dump_state()
        {
            await using PaprikaStateFactory stateDb = new();
            WorldState provider = new(stateDb, _codeDb, Logger);
            provider.CreateAccount(TestItem.AddressA, 1.Ether());
            provider.Commit(MuirGlacier.Instance);
            provider.CommitTree(0);

            string state = provider.DumpState();
            state.Should().NotBeEmpty();
        }

        [Test]
        public async Task Can_accepts_visitors()
        {
            await using PaprikaStateFactory stateDb = new();
            WorldState provider = new(stateDb, Substitute.For<IDb>(), Logger);
            provider.CreateAccount(TestItem.AddressA, 1.Ether());
            provider.Commit(MuirGlacier.Instance);
            provider.CommitTree(0);

            TrieStatsCollector visitor = new(new MemDb(), LimboLogs.Instance);
            provider.Accept(visitor, provider.StateRoot);
        }

        [Test]
        public async Task Empty_commit_restore()
        {
            await using PaprikaStateFactory stateDb = new();
            WorldState provider = new(stateDb, _codeDb, Logger);
            provider.Commit(Frontier.Instance);
            provider.Restore(Snapshot.Empty);
        }

        [Test]
        public async Task Update_balance_on_non_existing_account_throws()
        {
            await using PaprikaStateFactory stateDb = new();
            WorldState provider = new(stateDb, _codeDb, Logger);
            Assert.Throws<InvalidOperationException>(() => provider.AddToBalance(TestItem.AddressA, 1.Ether(), Olympic.Instance));
        }

        [Test]
        public async Task Is_empty_account()
        {
            await using PaprikaStateFactory stateDb = new();
            WorldState provider = new(stateDb, _codeDb, Logger);
            provider.CreateAccount(_address1, 0);
            provider.Commit(Frontier.Instance);
            Assert.True(provider.IsEmptyAccount(_address1));
        }

        [Test]
        public async Task Returns_empty_byte_code_for_non_existing_accounts()
        {
            await using PaprikaStateFactory stateDb = new();
            WorldState provider = new(stateDb, _codeDb, Logger);
            byte[] code = provider.GetCode(TestItem.AddressA);
            code.Should().BeEmpty();
        }

        [Test]
        public async Task Restore_update_restore()
        {
            await using PaprikaStateFactory stateDb = new();
            WorldState provider = new(stateDb, _codeDb, Logger);
            provider.CreateAccount(_address1, 0);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.Restore(new Snapshot(4, Snapshot.Storage.Empty));
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.Restore(new Snapshot(4, Snapshot.Storage.Empty));
            Assert.That(provider.GetBalance(_address1), Is.EqualTo((UInt256)4));
        }

        [Test]
        public async Task Keep_in_cache()
        {
            await using PaprikaStateFactory stateDb = new();
            WorldState provider = new(stateDb, _codeDb, Logger);
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
        public async Task Restore_in_the_middle()
        {
            await using PaprikaStateFactory stateDb = new();
            byte[] code = new byte[] { 1 };

            WorldState provider = new(stateDb, _codeDb, Logger);
            provider.CreateAccount(_address1, 1);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.IncrementNonce(_address1);
            provider.InsertCode(_address1, new byte[] { 1 }, Frontier.Instance);
            provider.UpdateStorageRoot(_address1, Hash2);

            Assert.That(provider.GetNonce(_address1), Is.EqualTo(UInt256.One));
            Assert.That(provider.GetBalance(_address1), Is.EqualTo(UInt256.One + 1));
            Assert.That(provider.GetCode(_address1), Is.EqualTo(code));
            provider.Restore(new Snapshot(4, Snapshot.Storage.Empty));
            Assert.That(provider.GetNonce(_address1), Is.EqualTo(UInt256.One));
            Assert.That(provider.GetBalance(_address1), Is.EqualTo(UInt256.One + 1));
            Assert.That(provider.GetCode(_address1), Is.EqualTo(code));
            provider.Restore(new Snapshot(3, Snapshot.Storage.Empty));
            Assert.That(provider.GetNonce(_address1), Is.EqualTo(UInt256.One));
            Assert.That(provider.GetBalance(_address1), Is.EqualTo(UInt256.One + 1));
            Assert.That(provider.GetCode(_address1), Is.EqualTo(code));
            provider.Restore(new Snapshot(2, Snapshot.Storage.Empty));
            Assert.That(provider.GetNonce(_address1), Is.EqualTo(UInt256.One));
            Assert.That(provider.GetBalance(_address1), Is.EqualTo(UInt256.One + 1));
            Assert.That(provider.GetCode(_address1), Is.EqualTo(new byte[0]));
            provider.Restore(new Snapshot(1, Snapshot.Storage.Empty));
            Assert.That(provider.GetNonce(_address1), Is.EqualTo(UInt256.Zero));
            Assert.That(provider.GetBalance(_address1), Is.EqualTo(UInt256.One + 1));
            Assert.That(provider.GetCode(_address1), Is.EqualTo(new byte[0]));
            provider.Restore(new Snapshot(0, Snapshot.Storage.Empty));
            Assert.That(provider.GetNonce(_address1), Is.EqualTo(UInt256.Zero));
            Assert.That(provider.GetBalance(_address1), Is.EqualTo(UInt256.One));
            Assert.That(provider.GetCode(_address1), Is.EqualTo(new byte[0]));
            provider.Restore(new Snapshot(-1, Snapshot.Storage.Empty));
            Assert.That(provider.AccountExists(_address1), Is.EqualTo(false));
        }

        [Test(Description = "It was failing before as touch was marking the accounts as committed but not adding to trace list")]
        public async Task Touch_empty_trace_does_not_throw()
        {
            await using PaprikaStateFactory stateDb = new();
            ParityLikeTxTracer tracer = new(Build.A.Block.TestObject, null, ParityTraceTypes.StateDiff);

            WorldState provider = new(stateDb, _codeDb, Logger);
            provider.CreateAccount(_address1, 0);
            AccountStruct account = provider.GetAccount(_address1);
            Assert.True(account.IsEmpty);
            provider.Commit(Frontier.Instance); // commit empty account (before the empty account fix in Spurious Dragon)
            Assert.True(provider.AccountExists(_address1));

            provider.Reset(); // clear all caches

            provider.GetBalance(_address1); // justcache
            provider.AddToBalance(_address1, 0, SpuriousDragon.Instance); // touch
            Assert.DoesNotThrow(() => provider.Commit(SpuriousDragon.Instance, tracer));
        }

        [Test]
        public async Task Does_not_require_recalculation_after_reset()
        {
            await using PaprikaStateFactory stateDb = new();
            WorldState provider = new(stateDb, _codeDb, Logger);
            provider.CreateAccount(TestItem.AddressA, 5);

            Action action = () => { _ = provider.StateRoot; };
            action.Should().Throw<InvalidOperationException>();

            provider.Reset();
            action.Should().NotThrow<InvalidOperationException>();
        }
    }
}
