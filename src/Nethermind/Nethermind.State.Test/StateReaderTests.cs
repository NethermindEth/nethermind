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
using Nethermind.Core.Test;
using System.Collections;

namespace Nethermind.Store.Test
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class StateReaderTests
    {
        private static readonly Keccak Hash1 = Keccak.Compute("1");
        private readonly Address _address1 = new(Hash1);
        private static readonly ILogManager Logger = NUnitLogManager.Instance;

        [Test]
        [TestCaseSource(typeof(Instances))]
        public async Task CanAskAboutBalanceInParallel(string name, ITrieStore trieStore)
        {
            IReleaseSpec spec = MainnetSpecProvider.Instance.GetSpec((ForkActivation)MainnetSpecProvider.ConstantinopleFixBlockNumber);
            WorldState provider = new(trieStore, Substitute.For<IDb>(), Logger);
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
                new(trieStore, Substitute.For<IDb>(), Logger);

            Task a = StartTask(reader, stateRoot0, 1);
            Task b = StartTask(reader, stateRoot1, 2);
            Task c = StartTask(reader, stateRoot2, 3);
            Task d = StartTask(reader, stateRoot3, 4);

            await Task.WhenAll(a, b, c, d);
        }

        [Test]
        [TestCaseSource(typeof(Instances))]
        public async Task CanAskAboutStorageInParallel(string name, ITrieStore trieStore)
        {
            StorageCell storageCell = new(_address1, UInt256.One);
            IReleaseSpec spec = MuirGlacier.Instance;
            WorldState provider = new(trieStore, new MemDb(), Logger);

            void UpdateStorageValue(byte[] newValue)
            {
                provider.Set(storageCell, newValue);
            }

            void AddOneToBalance()
            {
                provider.AddToBalance(_address1, 1, spec);
            }

            void CommitEverything(long blockNumber)
            {
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
                new(trieStore, Substitute.For<IDb>(), Logger);

            Task a = StartStorageTask(reader, stateRoot0, storageCell, new byte[] { 1 });
            Task b = StartStorageTask(reader, stateRoot1, storageCell, new byte[] { 2 });
            Task c = StartStorageTask(reader, stateRoot2, storageCell, new byte[] { 3 });
            Task d = StartStorageTask(reader, stateRoot3, storageCell, new byte[] { 4 });

            await Task.WhenAll(a, b, c, d);
        }

        [Test]
        [TestCaseSource(typeof(Instances))]
        public void NonExisting(string name, ITrieStore trieStore)
        {
            StorageCell storageCell = new(_address1, UInt256.One);
            IReleaseSpec spec = MuirGlacier.Instance;

            WorldState provider = new(trieStore, new MemDb(), Logger);

            void CommitEverything()
            {
                provider.Commit(spec);
                provider.CommitTree(0);
            }

            provider.CreateAccount(_address1, 1);
            provider.Set(storageCell, new byte[] { 1 });
            CommitEverything();
            Keccak stateRoot0 = provider.StateRoot;

            StateReader reader =
                new(trieStore, Substitute.For<IDb>(), Logger);
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
                        Assert.That(balance, Is.EqualTo(value));
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
                        Keccak storageRoot = reader.GetStorageRoot(stateRoot, storageCell.Address);
                        byte[] result = reader.GetStorage(storageRoot, storageCell.Address, storageCell.Index);
                        result.Should().BeEquivalentTo(value);
                    }
                });
        }

        [Test]
        [TestCaseSource(typeof(Instances))]
        public async Task GetStorage(string name, ITrieStore trieStore)
        {
            IDbProvider dbProvider = await TestMemDbProvider.InitAsync();

            /* all testing will be touching just a single storage cell */
            StorageCell storageCell = new(_address1, UInt256.One);

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

            StateReader reader = new(trieStore, dbProvider.CodeDb, Logger);

            var account = reader.GetAccount(state.StateRoot, _address1);
            var retrieved = reader.GetStorage(account.StorageRoot, _address1, storageCell.Index);
            //byte[] retrieved = reader.GetStorage(state.StateRoot, _address1, storageCell.Index);
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

            retrieved =
                reader.GetStorage(processorStateProvider.GetStorageRoot(storageCell.Address), storageCell.Address, storageCell.Index);
            retrieved.Should().BeEquivalentTo(newValue);

            /* If it failed then it means that the blockchain bridge cached the previous call value */
        }

        internal class Instances : IEnumerable
        {
            public static IEnumerable TestCases
            {
                get
                {
                    yield return new TestCaseData("Keccak Store", new TrieStore(new MemDb(), Logger));
                    yield return new TestCaseData("Path Store", new TrieStoreByPath(new MemColumnsDb<StateColumns>(), Persist.IfBlockOlderThan(128), Logger));
                }
            }

            public IEnumerator GetEnumerator() => TestCases.GetEnumerator();
        }
    }
}
