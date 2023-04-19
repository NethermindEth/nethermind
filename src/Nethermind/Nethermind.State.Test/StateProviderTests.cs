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
using System.Threading;

namespace Nethermind.Store.Test
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class StateProviderTests
    {
        private static readonly Keccak Hash1 = Keccak.Compute("1");
        private static readonly Keccak Hash2 = Keccak.Compute("2");
        private readonly Address _address1 = new(Hash1);
        private static readonly ILogManager Logger = LimboLogs.Instance;
        private IDb _codeDb;

        [SetUp]
        public void Setup()
        {
            _codeDb = new MemDb();
        }

        private static (string, ITrieStore)[] _variants;
        public static (string name, ITrieStore trieStore)[] Variants
            => LazyInitializer.EnsureInitialized(ref _variants, InitVariants);

        public static (string Name, ITrieStore TrieStore)[] InitVariants()
        {
            return new (string, ITrieStore)[]
            {
                ("Keccak Store", new TrieStore(new MemDb(), Logger)),
                ("Path Store", new TrieStoreByPath(new MemDb(), Logger))
            };
        }

        [Test]
        [TestCaseSource(nameof(Variants))]
        public void Eip_158_zero_value_transfer_deletes((string Name, ITrieStore TrieStore) testCase)
        {
            StateProvider frontierProvider = new(testCase.TrieStore, _codeDb, Logger);
            frontierProvider.CreateAccount(_address1, 0);
            frontierProvider.Commit(Frontier.Instance);
            frontierProvider.CommitTree(0);

            StateProvider provider = new(testCase.TrieStore, _codeDb, Logger);
            provider.StateRoot = frontierProvider.StateRoot;

            provider.AddToBalance(_address1, 0, SpuriousDragon.Instance);
            provider.Commit(SpuriousDragon.Instance);
            Assert.False(provider.AccountExists(_address1));

            _codeDb = Substitute.For<IDb>();
        }

        [Test]
        public void Eip_158_touch_zero_value_system_account_is_not_deleted()
        {
            TrieStoreByPath trieStore = new(new MemDb(), Logger);
            StateProvider provider = new(trieStore, _codeDb, Logger);
            var systemUser = Address.SystemUser;

            provider.CreateAccount(systemUser, 0);
            provider.Commit(Homestead.Instance);

            var releaseSpec = new ReleaseSpec() { IsEip158Enabled = true };
            provider.UpdateCodeHash(systemUser, Keccak.OfAnEmptyString, releaseSpec);
            provider.Commit(releaseSpec);

            provider.GetAccount(systemUser).Should().NotBeNull();
        }

        [Test]
        [TestCaseSource(nameof(Variants))]
        public void Can_dump_state((string Name, ITrieStore TrieStore) testCase)
        {
            StateProvider provider = new(testCase.TrieStore, _codeDb, Logger);
            provider.CreateAccount(TestItem.AddressA, 1.Ether());
            provider.Commit(MuirGlacier.Instance);
            provider.CommitTree(0);

            string state = provider.DumpState();
            state.Should().NotBeEmpty();
        }

        [Test]
        [TestCaseSource(nameof(Variants))]
        public void Can_collect_stats((string Name, ITrieStore TrieStore) testCase)
        {
            StateProvider provider = new(testCase.TrieStore, _codeDb, Logger);
            provider.CreateAccount(TestItem.AddressA, 1.Ether());
            provider.Commit(MuirGlacier.Instance);
            provider.CommitTree(0);

            var stats = provider.CollectStats(_codeDb, Logger);
            stats.AccountCount.Should().Be(1);
        }

        [Test]
        [TestCaseSource(nameof(Variants))]
        public void Can_accepts_visitors((string Name, ITrieStore TrieStore) testCase)
        {
            StateProvider provider = new(testCase.TrieStore, _codeDb, Logger);
            provider.CreateAccount(TestItem.AddressA, 1.Ether());
            provider.Commit(MuirGlacier.Instance);
            provider.CommitTree(0);

            TrieStatsCollector visitor = new(new MemDb(), LimboLogs.Instance);
            provider.Accept(visitor, provider.StateRoot);
        }

        [Test]
        [TestCaseSource(nameof(Variants))]
        public void Empty_commit_restore((string Name, ITrieStore TrieStore) testCase)
        {
            StateProvider provider = new(testCase.TrieStore, _codeDb, Logger);
            provider.Commit(Frontier.Instance);
            provider.Restore(-1);
        }

        [Test]
        [TestCaseSource(nameof(Variants))]
        public void Update_balance_on_non_existing_account_throws((string Name, ITrieStore TrieStore) testCase)
        {
            StateProvider provider = new(testCase.TrieStore, _codeDb, Logger);
            Assert.Throws<InvalidOperationException>(() => provider.AddToBalance(TestItem.AddressA, 1.Ether(), Olympic.Instance));
        }

        [Test]
        [TestCaseSource(nameof(Variants))]
        public void Is_empty_account((string Name, ITrieStore TrieStore) testCase)
        {
            StateProvider provider = new(testCase.TrieStore, _codeDb, Logger);
            provider.CreateAccount(_address1, 0);
            provider.Commit(Frontier.Instance);
            Assert.True(provider.IsEmptyAccount(_address1));
        }

        [Test]
        [TestCaseSource(nameof(Variants))]
        public void Returns_empty_byte_code_for_non_existing_accounts((string Name, ITrieStore TrieStore) testCase)
        {
            StateProvider provider = new(testCase.TrieStore, _codeDb, Logger);
            byte[] code = provider.GetCode(TestItem.AddressA);
            code.Should().BeEmpty();
        }

        [Test]
        [TestCaseSource(nameof(Variants))]
        public void Restore_update_restore((string Name, ITrieStore TrieStore) testCase)
        {
            StateProvider provider = new(testCase.TrieStore, _codeDb, Logger);
            provider.CreateAccount(_address1, 0);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.Restore(4);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.Restore(4);
            Assert.AreEqual((UInt256)4, provider.GetBalance(_address1));
        }

        [Test]
        [TestCaseSource(nameof(Variants))]
        public void Keep_in_cache((string Name, ITrieStore TrieStore) testCase)
        {
            StateProvider provider = new(testCase.TrieStore, _codeDb, Logger);
            provider.CreateAccount(_address1, 0);
            provider.Commit(Frontier.Instance);
            provider.GetBalance(_address1);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.Restore(-1);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.Restore(-1);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.Restore(-1);
            Assert.AreEqual(UInt256.Zero, provider.GetBalance(_address1));
        }

        [Test]
        [TestCaseSource(nameof(Variants))]
        public void Restore_in_the_middle((string Name, ITrieStore TrieStore) testCase)
        {
            byte[] code = new byte[] { 1 };

            StateProvider provider = new(testCase.TrieStore, _codeDb, Logger);
            provider.CreateAccount(_address1, 1);
            provider.AddToBalance(_address1, 1, Frontier.Instance);
            provider.IncrementNonce(_address1);
            Keccak codeHash = provider.UpdateCode(new byte[] { 1 });
            provider.UpdateCodeHash(_address1, codeHash, Frontier.Instance);
            provider.UpdateStorageRoot(_address1, Hash2);

            Assert.AreEqual(UInt256.One, provider.GetNonce(_address1));
            Assert.AreEqual(UInt256.One + 1, provider.GetBalance(_address1));
            Assert.AreEqual(code, provider.GetCode(_address1));
            provider.Restore(4);
            Assert.AreEqual(UInt256.One, provider.GetNonce(_address1));
            Assert.AreEqual(UInt256.One + 1, provider.GetBalance(_address1));
            Assert.AreEqual(code, provider.GetCode(_address1));
            provider.Restore(3);
            Assert.AreEqual(UInt256.One, provider.GetNonce(_address1));
            Assert.AreEqual(UInt256.One + 1, provider.GetBalance(_address1));
            Assert.AreEqual(code, provider.GetCode(_address1));
            provider.Restore(2);
            Assert.AreEqual(UInt256.One, provider.GetNonce(_address1));
            Assert.AreEqual(UInt256.One + 1, provider.GetBalance(_address1));
            Assert.AreEqual(new byte[0], provider.GetCode(_address1));
            provider.Restore(1);
            Assert.AreEqual(UInt256.Zero, provider.GetNonce(_address1));
            Assert.AreEqual(UInt256.One + 1, provider.GetBalance(_address1));
            Assert.AreEqual(new byte[0], provider.GetCode(_address1));
            provider.Restore(0);
            Assert.AreEqual(UInt256.Zero, provider.GetNonce(_address1));
            Assert.AreEqual(UInt256.One, provider.GetBalance(_address1));
            Assert.AreEqual(new byte[0], provider.GetCode(_address1));
            provider.Restore(-1);
            Assert.AreEqual(false, provider.AccountExists(_address1));
        }

        [Test(Description = "It was failing before as touch was marking the accounts as committed but not adding to trace list")]
        [TestCaseSource(nameof(Variants))]
        public void Touch_empty_trace_does_not_throw((string Name, ITrieStore TrieStore) testCase)
        {
            ParityLikeTxTracer tracer = new(Build.A.Block.TestObject, null, ParityTraceTypes.StateDiff);

            StateProvider provider = new(testCase.TrieStore, _codeDb, Logger);
            provider.CreateAccount(_address1, 0);
            Account account = provider.GetAccount(_address1);
            Assert.True(account.IsEmpty);
            provider.Commit(Frontier.Instance); // commit empty account (before the empty account fix in Spurious Dragon)
            Assert.True(provider.AccountExists(_address1));

            provider.Reset(); // clear all caches

            provider.GetBalance(_address1); // justcache
            provider.AddToBalance(_address1, 0, SpuriousDragon.Instance); // touch
            Assert.DoesNotThrow(() => provider.Commit(SpuriousDragon.Instance, tracer));
        }

        [Test]
        [TestCaseSource(nameof(Variants))]
        public void Does_not_require_recalculation_after_reset((string Name, ITrieStore TrieStore) testCase)
        {
            StateProvider provider = new(testCase.TrieStore, _codeDb, Logger);
            provider.CreateAccount(TestItem.AddressA, 5);

            Action action = () => { _ = provider.StateRoot; };
            action.Should().Throw<InvalidOperationException>();

            provider.Reset();
            action.Should().NotThrow<InvalidOperationException>();
        }
    }
}
