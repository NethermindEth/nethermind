// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;
using System.Threading;
using Nethermind.Trie.ByPath;

namespace Nethermind.Store.Test
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class StateReaderTests
    {
        private static readonly Keccak Hash1 = Keccak.Compute("1");
        private readonly Address _address1 = new(Hash1);
        private static readonly ILogManager Logger = LimboLogs.Instance;

        private static (string, ITrieStore)[] _variants;
        public static (string name, ITrieStore trieStore)[] Variants
            => LazyInitializer.EnsureInitialized(ref _variants, InitVariants);

        public static (string Name, ITrieStore TrieStore)[] InitVariants()
        {
            return new (string, ITrieStore)[]
            {
                ("Keccak Store", new TrieStore(new MemDb(), Logger)),
                ("Path Store", new TrieStoreByPath(new MemDb(), Trie.Pruning.No.Pruning, Persist.EveryBlock, Logger))
            };
        }

        [Test]
        [TestCaseSource(nameof(Variants))]
        public async Task Can_ask_about_balance_in_parallel((string Name, ITrieStore TrieStore) testCase)
        {
            IReleaseSpec spec = MainnetSpecProvider.Instance.GetSpec((ForkActivation)MainnetSpecProvider.ConstantinopleFixBlockNumber);
            MemDb stateDb = new();
            StateProvider provider =
                new(testCase.TrieStore, Substitute.For<IDb>(), Logger);
            provider.CreateAccount(_address1, 0);
            provider.AddToBalance(_address1, 1, spec);
            provider.Commit(spec);
            provider.CommitTree(0);
            Keccak stateRoot0 = provider.StateRoot;

            provider.AddToBalance(_address1, 1, spec);
            provider.Commit(spec);
            provider.CommitTree(1);
            Keccak stateRoot1 = provider.StateRoot;

            provider.AddToBalance(_address1, 1, spec);
            provider.Commit(spec);
            provider.CommitTree(2);
            Keccak stateRoot2 = provider.StateRoot;

            provider.AddToBalance(_address1, 1, spec);
            provider.Commit(spec);
            provider.CommitTree(3);
            Keccak stateRoot3 = provider.StateRoot;

            //provider.CommitTree(0);

            StateReader reader =
                new(testCase.TrieStore, Substitute.For<IDb>(), Logger);

            Task a = StartTask(reader, stateRoot0, 1);
            //Task b = StartTask(reader, stateRoot1, 2);
            //Task c = StartTask(reader, stateRoot2, 3);
            //Task d = StartTask(reader, stateRoot3, 4);

            //await Task.WhenAll(a, b, c, d);
            await Task.WhenAll(a);
        }

        [Test]
        [TestCaseSource(nameof(Variants))]
        public async Task Can_ask_about_storage_in_parallel((string Name, ITrieStore TrieStore) testCase)
        {
            StorageCell storageCell = new(_address1, UInt256.One);
            IReleaseSpec spec = MuirGlacier.Instance;
            MemDb stateDb = new();
            TrieStore trieStore = new(stateDb, Logger);
            StateProvider provider = new(testCase.TrieStore, new MemDb(), Logger);
            StorageProvider storageProvider = new(trieStore, provider, Logger);

            void UpdateStorageValue(byte[] newValue)
            {
                storageProvider.Set(storageCell, newValue);
            }

            void AddOneToBalance()
            {
                provider.AddToBalance(_address1, 1, spec);
            }

            void CommitEverything(long blockNumber)
            {
                storageProvider.Commit();
                storageProvider.CommitTrees(blockNumber);
                provider.Commit(spec);
                provider.CommitTree(blockNumber);
            }

            provider.CreateAccount(_address1, 1);
            CommitEverything(0);

            AddOneToBalance();
            UpdateStorageValue(new byte[] { 1 });
            CommitEverything(1);
            Keccak stateRoot0 = provider.StateRoot;

            AddOneToBalance();
            UpdateStorageValue(new byte[] { 2 });
            CommitEverything(2);
            Keccak stateRoot1 = provider.StateRoot;

            AddOneToBalance();
            UpdateStorageValue(new byte[] { 3 });
            CommitEverything(3);
            Keccak stateRoot2 = provider.StateRoot;

            AddOneToBalance();
            UpdateStorageValue(new byte[] { 4 });
            CommitEverything(4);
            Keccak stateRoot3 = provider.StateRoot;

            StateReader reader =
                new(testCase.TrieStore, Substitute.For<IDb>(), Logger);

            Task a = StartStorageTask(reader, stateRoot0, storageCell, new byte[] { 1 });
            Task b = StartStorageTask(reader, stateRoot1, storageCell, new byte[] { 2 });
            Task c = StartStorageTask(reader, stateRoot2, storageCell, new byte[] { 3 });
            Task d = StartStorageTask(reader, stateRoot3, storageCell, new byte[] { 4 });

            await Task.WhenAll(a, b, c, d);
        }

        [Test]
        [TestCaseSource(nameof(Variants))]
        public void Non_existing((string Name, ITrieStore TrieStore) testCase)
        {
            StorageCell storageCell = new(_address1, UInt256.One);
            IReleaseSpec spec = MuirGlacier.Instance;

            MemDb stateDb = new();
            TrieStore trieStore = new(stateDb, Logger);
            StateProvider provider = new(testCase.TrieStore, new MemDb(), Logger);
            StorageProvider storageProvider = new(trieStore, provider, Logger);

            void CommitEverything()
            {
                storageProvider.Commit();
                storageProvider.CommitTrees(0);
                provider.Commit(spec);
                provider.CommitTree(0);
            }

            provider.CreateAccount(_address1, 1);
            storageProvider.Set(storageCell, new byte[] { 1 });
            CommitEverything();
            Keccak stateRoot0 = provider.StateRoot;

            StateReader reader =
                new(testCase.TrieStore, Substitute.For<IDb>(), Logger);
            reader.GetStorage(stateRoot0, _address1, storageCell.Index + 1).Should().BeEquivalentTo(new byte[] { 0 });
        }

        private Task StartTask(StateReader reader, Keccak stateRoot, UInt256 value)
        {
            return Task.Run(
                () =>
                {
                    for (int i = 0; i < 10000; i++)
                    {
                        UInt256 balance = reader.GetBalance(stateRoot, _address1);
                        Assert.AreEqual(value, balance);
                    }
                });
        }

        private Task StartStorageTask(StateReader reader, Keccak stateRoot, StorageCell storageCell, byte[] value)
        {
            return Task.Run(
                () =>
                {
                    for (int i = 0; i < 1000; i++)
                    {
                        byte[] result = reader.GetStorage(stateRoot, storageCell.Address, storageCell.Index);
                        result.Should().BeEquivalentTo(value);
                    }
                });
        }

        [Test]
        [TestCaseSource(nameof(Variants))]
        public async Task Get_storage((string Name, ITrieStore TrieStore) testCase)
        {
            IDbProvider dbProvider = await TestMemDbProvider.InitAsync();

            /* all testing will be touching just a single storage cell */
            StorageCell storageCell = new(_address1, UInt256.One);

            TrieStore trieStore = new(dbProvider.StateDb, Logger);
            StateProvider state = new(testCase.TrieStore, dbProvider.CodeDb, Logger);
            StorageProvider storage = new(trieStore, state, Logger);

            /* to start with we need to create an account that we will be setting storage at */
            state.CreateAccount(storageCell.Address, UInt256.One);
            state.Commit(MuirGlacier.Instance);
            state.CommitTree(1);

            /* at this stage we have an account with empty storage at the address that we want to test */

            byte[] initialValue = new byte[] { 1, 2, 3 };
            storage.Set(storageCell, initialValue);
            storage.Commit();
            storage.CommitTrees(2);
            state.Commit(MuirGlacier.Instance);
            state.CommitTree(2);

            StateReader reader = new(testCase.TrieStore, dbProvider.CodeDb, Logger);

            var retrieved = reader.GetStorage(state.StateRoot, _address1, storageCell.Index);
            retrieved.Should().BeEquivalentTo(initialValue);

            /* at this stage we set the value in storage to 1,2,3 at the tested storage cell */

            /* Now we are testing scenario where the storage is being changed by the block processor.
               To do that we create some different storage / state access stack that represents the processor.
               It is a different stack of objects than the one that is used by the blockchain bridge. */

            byte[] newValue = new byte[] { 1, 2, 3, 4, 5 };

            StateProvider processorStateProvider =
                new(testCase.TrieStore, new MemDb(), LimboLogs.Instance);
            processorStateProvider.StateRoot = state.StateRoot;

            StorageProvider processorStorageProvider =
                new(trieStore, processorStateProvider, LimboLogs.Instance);

            processorStorageProvider.Set(storageCell, newValue);
            processorStorageProvider.Commit();
            processorStorageProvider.CommitTrees(3);
            processorStateProvider.Commit(MuirGlacier.Instance);
            processorStateProvider.CommitTree(3);

            /* At this stage the DB should have the storage value updated to 5.
               We will try to retrieve the value by taking the state root from the processor.*/

            retrieved =
                reader.GetStorage(storageCell.Address, storageCell.Index);
            retrieved.Should().BeEquivalentTo(newValue);

            /* If it failed then it means that the blockchain bridge cached the previous call value */
        }
    }
}
