// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class StateReaderTests
    {
        private static readonly Hash256 Hash1 = Keccak.Compute("1");
        private readonly Address _address1 = new(Hash1);
        private static readonly ILogManager Logger = LimboLogs.Instance;

        [Test]
        public async Task Can_ask_about_balance_in_parallel()
        {
            IReleaseSpec spec = MainnetSpecProvider.Instance.GetSpec((ForkActivation)MainnetSpecProvider.ConstantinopleFixBlockNumber);
            MemDb stateDb = new();
            WorldState provider =
                new(TestTrieStoreFactory.Build(stateDb, Logger), Substitute.For<IDb>(), Logger);
            provider.CreateAccount(_address1, 0);
            provider.AddToBalance(_address1, 1, spec);
            provider.Commit(spec);
            provider.CommitTree(0);
            Hash256 stateRoot0 = provider.StateRoot;

            provider.AddToBalance(_address1, 1, spec);
            provider.Commit(spec);
            provider.CommitTree(0);
            Hash256 stateRoot1 = provider.StateRoot;

            provider.AddToBalance(_address1, 1, spec);
            provider.Commit(spec);
            provider.CommitTree(0);
            Hash256 stateRoot2 = provider.StateRoot;

            provider.AddToBalance(_address1, 1, spec);
            provider.Commit(spec);
            provider.CommitTree(0);
            Hash256 stateRoot3 = provider.StateRoot;

            provider.CommitTree(0);

            StateReader reader =
                new(TestTrieStoreFactory.Build(stateDb, LimboLogs.Instance), Substitute.For<IDb>(), Logger);

            Task a = StartTask(reader, stateRoot0, 1);
            Task b = StartTask(reader, stateRoot1, 2);
            Task c = StartTask(reader, stateRoot2, 3);
            Task d = StartTask(reader, stateRoot3, 4);

            await Task.WhenAll(a, b, c, d);
        }

        [Test]
        public async Task Can_ask_about_storage_in_parallel()
        {
            StorageCell storageCell = new(_address1, UInt256.One);
            IReleaseSpec spec = MuirGlacier.Instance;
            MemDb stateDb = new();
            TrieStore trieStore = TestTrieStoreFactory.Build(stateDb, Logger);
            WorldState provider = new(trieStore, new MemDb(), Logger);

            void UpdateStorageValue(byte[] newValue)
            {
                provider.Set(storageCell, newValue);
            }

            void AddOneToBalance()
            {
                provider.AddToBalance(_address1, 1, spec);
            }

            void CommitEverything()
            {
                provider.Commit(spec);
                provider.CommitTree(0);
            }

            provider.CreateAccount(_address1, 1);
            CommitEverything();

            AddOneToBalance();
            UpdateStorageValue(new byte[] { 1 });
            CommitEverything();
            Hash256 stateRoot0 = provider.StateRoot;

            AddOneToBalance();
            UpdateStorageValue(new byte[] { 2 });
            CommitEverything();
            Hash256 stateRoot1 = provider.StateRoot;

            AddOneToBalance();
            UpdateStorageValue(new byte[] { 3 });
            CommitEverything();
            Hash256 stateRoot2 = provider.StateRoot;

            AddOneToBalance();
            UpdateStorageValue(new byte[] { 4 });
            CommitEverything();
            Hash256 stateRoot3 = provider.StateRoot;

            StateReader reader =
                new(TestTrieStoreFactory.Build(stateDb, LimboLogs.Instance), Substitute.For<IDb>(), Logger);

            Task a = StartStorageTask(reader, stateRoot0, storageCell, new byte[] { 1 });
            Task b = StartStorageTask(reader, stateRoot1, storageCell, new byte[] { 2 });
            Task c = StartStorageTask(reader, stateRoot2, storageCell, new byte[] { 3 });
            Task d = StartStorageTask(reader, stateRoot3, storageCell, new byte[] { 4 });

            await Task.WhenAll(a, b, c, d);
        }

        [Test]
        public void Non_existing()
        {
            StorageCell storageCell = new(_address1, UInt256.One);
            IReleaseSpec spec = MuirGlacier.Instance;

            MemDb stateDb = new();
            TrieStore trieStore = TestTrieStoreFactory.Build(stateDb, Logger);
            WorldState provider = new(trieStore, new MemDb(), Logger);

            void CommitEverything()
            {
                provider.Commit(spec);
                provider.CommitTree(0);
            }

            provider.CreateAccount(_address1, 1);
            provider.Set(storageCell, new byte[] { 1 });
            CommitEverything();
            Hash256 stateRoot0 = provider.StateRoot;

            StateReader reader =
                new(TestTrieStoreFactory.Build(stateDb, LimboLogs.Instance), Substitute.For<IDb>(), Logger);
            reader.GetStorage(stateRoot0, _address1, storageCell.Index + 1).ToArray().Should().BeEquivalentTo(new byte[] { 0 });
        }

        private Task StartTask(StateReader reader, Hash256 stateRoot, UInt256 value)
        {
            return Task.Run(
                () =>
                {
                    for (int i = 0; i < 10000; i++)
                    {
                        UInt256 balance = reader.GetBalance(stateRoot, _address1);
                        Assert.That(balance, Is.EqualTo(value));
                    }
                });
        }

        private Task StartStorageTask(StateReader reader, Hash256 stateRoot, StorageCell storageCell, byte[] value)
        {
            return Task.Run(
                () =>
                {
                    for (int i = 0; i < 1000; i++)
                    {
                        byte[] result = reader.GetStorage(stateRoot, storageCell.Address, storageCell.Index).ToArray();
                        result.Should().BeEquivalentTo(value);
                    }
                });
        }

        [Test]
        public async Task Get_storage()
        {
            IDbProvider dbProvider = await TestMemDbProvider.InitAsync();

            /* all testing will be touching just a single storage cell */
            StorageCell storageCell = new(_address1, UInt256.One);

            TrieStore trieStore = TestTrieStoreFactory.Build(dbProvider.StateDb, Logger);
            WorldState state = new(trieStore, dbProvider.CodeDb, Logger);

            /* to start with we need to create an account that we will be setting storage at */
            state.CreateAccount(storageCell.Address, UInt256.One);
            state.Commit(MuirGlacier.Instance);
            state.CommitTree(1);

            /* at this stage we have an account with empty storage at the address that we want to test */

            byte[] initialValue = new byte[] { 1, 2, 3 };
            state.Set(storageCell, initialValue);
            state.Commit(MuirGlacier.Instance);
            state.CommitTree(2);

            StateReader reader = new(
                TestTrieStoreFactory.Build(dbProvider.StateDb, LimboLogs.Instance), dbProvider.CodeDb, Logger);

            var retrieved = reader.GetStorage(state.StateRoot, _address1, storageCell.Index).ToArray();
            retrieved.Should().BeEquivalentTo(initialValue);

            /* at this stage we set the value in storage to 1,2,3 at the tested storage cell */

            /* Now we are testing scenario where the storage is being changed by the block processor.
               To do that we create some different storage / state access stack that represents the processor.
               It is a different stack of objects than the one that is used by the blockchain bridge. */

            byte[] newValue = new byte[] { 1, 2, 3, 4, 5 };

            WorldState processorStateProvider =
                new(trieStore, new MemDb(), LimboLogs.Instance);
            processorStateProvider.StateRoot = state.StateRoot;

            processorStateProvider.Set(storageCell, newValue);
            processorStateProvider.Commit(MuirGlacier.Instance);
            processorStateProvider.CommitTree(3);

            /* At this stage the DB should have the storage value updated to 5.
               We will try to retrieve the value by taking the state root from the processor.*/

            retrieved = reader.GetStorage(processorStateProvider.StateRoot, storageCell.Address, storageCell.Index).ToArray();
            retrieved.Should().BeEquivalentTo(newValue);

            /* If it failed then it means that the blockchain bridge cached the previous call value */
        }


        [Test]
        public void Can_collect_stats()
        {
            TrieStore trieStore = TestTrieStoreFactory.Build(new MemDb(), Logger);
            WorldState provider = new(trieStore, new MemDb(), Logger);
            provider.CreateAccount(TestItem.AddressA, 1.Ether());
            provider.Commit(MuirGlacier.Instance);
            provider.CommitTree(0);

            StateReader stateReader = new StateReader(trieStore.AsReadOnly(), new MemDb(), Logger);
            var stats = stateReader.CollectStats(provider.StateRoot, new MemDb(), Logger);
            stats.AccountCount.Should().Be(1);
        }

        [Test]
        public void IsInvalidContractSender_AccountHasCode_ReturnsTrue()
        {
            IReleaseSpec releaseSpec = Substitute.For<IReleaseSpec>();
            releaseSpec.IsEip3607Enabled.Returns(true);
            releaseSpec.IsEip7702Enabled.Returns(true);
            TrieStore trieStore = TestTrieStoreFactory.Build(new MemDb(), Logger);
            WorldState sut = new(trieStore, new MemDb(), Logger);
            sut.CreateAccount(TestItem.AddressA, 0);
            sut.InsertCode(TestItem.AddressA, ValueKeccak.Compute(new byte[1]), new byte[1], releaseSpec, false);
            sut.Commit(MuirGlacier.Instance);
            sut.CommitTree(0);

            bool result = sut.IsInvalidContractSender(releaseSpec, TestItem.AddressA);

            Assert.That(result, Is.True);
        }

        [Test]
        public void IsInvalidContractSender_AccountHasNoCode_ReturnsFalse()
        {
            IReleaseSpec releaseSpec = Substitute.For<IReleaseSpec>();
            releaseSpec.IsEip3607Enabled.Returns(true);
            releaseSpec.IsEip7702Enabled.Returns(true);
            TrieStore trieStore = TestTrieStoreFactory.Build(new MemDb(), Logger);
            WorldState sut = new(trieStore, new MemDb(), Logger);
            sut.CreateAccount(TestItem.AddressA, 0);
            sut.Commit(MuirGlacier.Instance);
            sut.CommitTree(0);

            bool result = sut.IsInvalidContractSender(releaseSpec, TestItem.AddressA);

            Assert.That(result, Is.False);
        }

        [Test]
        public void IsInvalidContractSender_AccountHasDelegatedCode_ReturnsFalse()
        {
            IReleaseSpec releaseSpec = Substitute.For<IReleaseSpec>();
            releaseSpec.IsEip3607Enabled.Returns(true);
            releaseSpec.IsEip7702Enabled.Returns(true);
            TrieStore trieStore = TestTrieStoreFactory.Build(new MemDb(), Logger);
            WorldState sut = new(trieStore, new MemDb(), Logger);
            sut.CreateAccount(TestItem.AddressA, 0);
            byte[] code = [.. Eip7702Constants.DelegationHeader, .. new byte[20]];
            sut.InsertCode(TestItem.AddressA, ValueKeccak.Compute(code), code, releaseSpec, false);
            sut.Commit(MuirGlacier.Instance);
            sut.CommitTree(0);

            bool result = sut.IsInvalidContractSender(releaseSpec, TestItem.AddressA);

            Assert.That(result, Is.False);
        }

        [Test]
        public void IsInvalidContractSender_AccountHasCodeButDelegateReturnsTrue_ReturnsFalse()
        {
            IReleaseSpec releaseSpec = Substitute.For<IReleaseSpec>();
            releaseSpec.IsEip3607Enabled.Returns(true);
            releaseSpec.IsEip7702Enabled.Returns(true);
            TrieStore trieStore = TestTrieStoreFactory.Build(new MemDb(), Logger);
            WorldState sut = new(trieStore, new MemDb(), Logger);
            sut.CreateAccount(TestItem.AddressA, 0);
            byte[] code = new byte[20];
            sut.InsertCode(TestItem.AddressA, ValueKeccak.Compute(code), code, releaseSpec, false);
            sut.Commit(MuirGlacier.Instance);
            sut.CommitTree(0);

            bool result = sut.IsInvalidContractSender(releaseSpec, TestItem.AddressA, static (_) => true);

            Assert.That(result, Is.False);
        }

        [Test]
        public void IsInvalidContractSender_AccountHasDelegatedCodeBut7702IsNotEnabled_ReturnsTrue()
        {
            IReleaseSpec releaseSpec = Substitute.For<IReleaseSpec>();
            releaseSpec.IsEip3607Enabled.Returns(true);
            TrieStore trieStore = TestTrieStoreFactory.Build(new MemDb(), Logger);
            WorldState sut = new(trieStore, new MemDb(), Logger);
            sut.CreateAccount(TestItem.AddressA, 0);
            byte[] code = [.. Eip7702Constants.DelegationHeader, .. new byte[20]];
            sut.InsertCode(TestItem.AddressA, ValueKeccak.Compute(code), code, releaseSpec, false);
            sut.Commit(MuirGlacier.Instance);
            sut.CommitTree(0);

            bool result = sut.IsInvalidContractSender(releaseSpec, TestItem.AddressA);

            Assert.That(result, Is.True);
        }

        [Test]
        public void IsInvalidContractSender_AccountHasDelegatedCodeBut3807IsNotEnabled_ReturnsFalse()
        {
            IReleaseSpec releaseSpec = Substitute.For<IReleaseSpec>();
            releaseSpec.IsEip7702Enabled.Returns(true);
            TrieStore trieStore = TestTrieStoreFactory.Build(new MemDb(), Logger);
            WorldState sut = new(trieStore, new MemDb(), Logger);
            sut.CreateAccount(TestItem.AddressA, 0);
            byte[] code = [.. Eip7702Constants.DelegationHeader, .. new byte[20]];
            sut.InsertCode(TestItem.AddressA, ValueKeccak.Compute(code), code, releaseSpec, false);
            sut.Commit(MuirGlacier.Instance);
            sut.CommitTree(0);

            bool result = sut.IsInvalidContractSender(releaseSpec, TestItem.AddressA);

            Assert.That(result, Is.False);
        }
    }
}
