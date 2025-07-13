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
using Nethermind.Evm.State;
using Nethermind.State;
using Nethermind.Trie;
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
            IDbProvider dbProvider = TestMemDbProvider.Init();
            IWorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest(dbProvider, LimboLogs.Instance);
            IWorldState provider = worldStateManager.GlobalWorldState;
            provider.CreateAccount(_address1, 0);
            provider.AddToBalance(_address1, 1, spec);
            provider.Commit(spec);
            provider.CommitTree(0);
            BlockHeader baseBlock0 = Build.A.BlockHeader.WithStateRoot(provider.StateRoot).TestObject;

            provider.AddToBalance(_address1, 1, spec);
            provider.Commit(spec);
            provider.CommitTree(0);
            BlockHeader baseBlock1 = Build.A.BlockHeader.WithStateRoot(provider.StateRoot).TestObject;

            provider.AddToBalance(_address1, 1, spec);
            provider.Commit(spec);
            provider.CommitTree(0);
            BlockHeader baseBlock2 = Build.A.BlockHeader.WithStateRoot(provider.StateRoot).TestObject;

            provider.AddToBalance(_address1, 1, spec);
            provider.Commit(spec);
            provider.CommitTree(0);
            BlockHeader baseBlock3 = Build.A.BlockHeader.WithStateRoot(provider.StateRoot).TestObject;

            provider.CommitTree(0);

            IStateReader reader = worldStateManager.GlobalStateReader;

            Task a = StartTask(reader, baseBlock0, 1);
            Task b = StartTask(reader, baseBlock1, 2);
            Task c = StartTask(reader, baseBlock2, 3);
            Task d = StartTask(reader, baseBlock3, 4);

            await Task.WhenAll(a, b, c, d);
        }

        [Test]
        public async Task Can_ask_about_storage_in_parallel()
        {
            StorageCell storageCell = new(_address1, UInt256.One);
            IReleaseSpec spec = MuirGlacier.Instance;
            IDbProvider dbProvider = TestMemDbProvider.Init();
            IWorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest(dbProvider, LimboLogs.Instance);
            IWorldState provider = worldStateManager.GlobalWorldState;

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
            BlockHeader baseBlock0 = Build.A.BlockHeader.WithStateRoot(provider.StateRoot).TestObject;

            AddOneToBalance();
            UpdateStorageValue(new byte[] { 2 });
            CommitEverything();
            BlockHeader baseBlock1 = Build.A.BlockHeader.WithStateRoot(provider.StateRoot).TestObject;

            AddOneToBalance();
            UpdateStorageValue(new byte[] { 3 });
            CommitEverything();
            BlockHeader baseBlock2 = Build.A.BlockHeader.WithStateRoot(provider.StateRoot).TestObject;

            AddOneToBalance();
            UpdateStorageValue(new byte[] { 4 });
            CommitEverything();
            BlockHeader baseBlock3 = Build.A.BlockHeader.WithStateRoot(provider.StateRoot).TestObject;

            IStateReader reader = worldStateManager.GlobalStateReader;

            Task a = StartStorageTask(reader, baseBlock0, storageCell, new byte[] { 1 });
            Task b = StartStorageTask(reader, baseBlock1, storageCell, new byte[] { 2 });
            Task c = StartStorageTask(reader, baseBlock2, storageCell, new byte[] { 3 });
            Task d = StartStorageTask(reader, baseBlock3, storageCell, new byte[] { 4 });

            await Task.WhenAll(a, b, c, d);
        }

        [Test]
        public void Non_existing()
        {
            StorageCell storageCell = new(_address1, UInt256.One);
            IReleaseSpec spec = MuirGlacier.Instance;

            IWorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest();
            IWorldState provider = worldStateManager.GlobalWorldState;

            void CommitEverything()
            {
                provider.Commit(spec);
                provider.CommitTree(0);
            }

            provider.CreateAccount(_address1, 1);
            provider.Set(storageCell, new byte[] { 1 });
            CommitEverything();
            Hash256 stateRoot0 = provider.StateRoot;

            IStateReader reader = worldStateManager.GlobalStateReader;
            reader.GetStorage(Build.A.BlockHeader.WithStateRoot(stateRoot0).TestObject, _address1, storageCell.Index + 1).ToArray().Should().BeEquivalentTo(new byte[] { 0 });
        }

        private Task StartTask(IStateReader reader, BlockHeader baseBlock, UInt256 value)
        {
            return Task.Run(
                () =>
                {
                    for (int i = 0; i < 10000; i++)
                    {
                        UInt256 balance = reader.GetBalance(baseBlock, _address1);
                        Assert.That(balance, Is.EqualTo(value));
                    }
                });
        }

        private Task StartStorageTask(IStateReader reader, BlockHeader baseBlock, StorageCell storageCell, byte[] value)
        {
            return Task.Run(
                () =>
                {
                    for (int i = 0; i < 1000; i++)
                    {
                        byte[] result = reader.GetStorage(baseBlock, storageCell.Address, storageCell.Index).ToArray();
                        result.Should().BeEquivalentTo(value);
                    }
                });
        }

        [Test]
        public void Get_storage()
        {
            /* all testing will be touching just a single storage cell */
            StorageCell storageCell = new(_address1, UInt256.One);

            IDbProvider dbProvider = TestMemDbProvider.Init();
            IWorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest(dbProvider, LimboLogs.Instance);
            IWorldState state = worldStateManager.GlobalWorldState;

            /* to start with we need to create an account that we will be setting storage at */
            state.CreateAccount(storageCell.Address, UInt256.One);
            state.Commit(MuirGlacier.Instance);
            state.CommitTree(1);

            /* at this stage we have an account with empty storage at the address that we want to test */

            byte[] initialValue = new byte[] { 1, 2, 3 };
            state.Set(storageCell, initialValue);
            state.Commit(MuirGlacier.Instance);
            state.CommitTree(2);
            BlockHeader baseBlock = Build.A.BlockHeader.WithNumber(2).WithStateRoot(state.StateRoot).TestObject;

            IStateReader reader = worldStateManager.GlobalStateReader;

            var retrieved = reader.GetStorage(baseBlock, _address1, storageCell.Index).ToArray();
            retrieved.Should().BeEquivalentTo(initialValue);

            /* at this stage we set the value in storage to 1,2,3 at the tested storage cell */

            /* Now we are testing scenario where the storage is being changed by the block processor.
               To do that we create some different storage / state access stack that represents the processor.
               It is a different stack of objects than the one that is used by the blockchain bridge. */
            // Note: There is only one global IWorldState and IStateReader now. With pruning trie store, the data is
            // not written to db immediately.

            byte[] newValue = new byte[] { 1, 2, 3, 4, 5 };

            IWorldState processorStateProvider = state; // They are the same
            processorStateProvider.SetBaseBlock(baseBlock);

            processorStateProvider.Set(storageCell, newValue);
            processorStateProvider.Commit(MuirGlacier.Instance);
            processorStateProvider.CommitTree(3);
            baseBlock = Build.A.BlockHeader.WithParent(baseBlock).WithStateRoot(state.StateRoot).TestObject;

            /* At this stage the DB should have the storage value updated to 5.
               We will try to retrieve the value by taking the state root from the processor.*/

            retrieved = reader.GetStorage(baseBlock, storageCell.Address, storageCell.Index).ToArray();
            retrieved.Should().BeEquivalentTo(newValue);

            /* If it failed then it means that the blockchain bridge cached the previous call value */
        }


        [Test]
        public void Can_collect_stats()
        {
            IDbProvider dbProvider = TestMemDbProvider.Init();
            IWorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest(dbProvider, LimboLogs.Instance);
            IWorldState provider = worldStateManager.GlobalWorldState;
            provider.CreateAccount(TestItem.AddressA, 1.Ether());
            provider.Commit(MuirGlacier.Instance);
            provider.CommitTree(0);

            IStateReader stateReader = worldStateManager.GlobalStateReader;
            var stats = stateReader.CollectStats(provider.StateRoot, new MemDb(), Logger);
            stats.AccountCount.Should().Be(1);
        }

        [Test]
        public void IsInvalidContractSender_AccountHasCode_ReturnsTrue()
        {
            IReleaseSpec releaseSpec = Substitute.For<IReleaseSpec>();
            releaseSpec.IsEip3607Enabled.Returns(true);
            releaseSpec.IsEip7702Enabled.Returns(true);
            IDbProvider dbProvider = TestMemDbProvider.Init();
            IWorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest(dbProvider, LimboLogs.Instance);
            IWorldState sut = worldStateManager.GlobalWorldState;
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
            IDbProvider dbProvider = TestMemDbProvider.Init();
            IWorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest(dbProvider, LimboLogs.Instance);
            IWorldState sut = worldStateManager.GlobalWorldState;
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
            IDbProvider dbProvider = TestMemDbProvider.Init();
            IWorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest(dbProvider, LimboLogs.Instance);
            IWorldState sut = worldStateManager.GlobalWorldState;
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
            IDbProvider dbProvider = TestMemDbProvider.Init();
            IWorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest(dbProvider, LimboLogs.Instance);
            IWorldState sut = worldStateManager.GlobalWorldState;
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
            IDbProvider dbProvider = TestMemDbProvider.Init();
            IWorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest(dbProvider, LimboLogs.Instance);
            IWorldState sut = worldStateManager.GlobalWorldState;
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
            IDbProvider dbProvider = TestMemDbProvider.Init();
            IWorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest(dbProvider, LimboLogs.Instance);
            IWorldState sut = worldStateManager.GlobalWorldState;
            sut.CreateAccount(TestItem.AddressA, 0);
            byte[] code = [.. Eip7702Constants.DelegationHeader, .. new byte[20]];
            sut.InsertCode(TestItem.AddressA, ValueKeccak.Compute(code), code, releaseSpec, false);
            sut.Commit(MuirGlacier.Instance);
            sut.CommitTree(0);

            bool result = sut.IsInvalidContractSender(releaseSpec, TestItem.AddressA);

            Assert.That(result, Is.False);
        }

        [Test]
        public void Can_accepts_visitors()
        {
            WorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest();
            IVisitingWorldState provider = worldStateManager.GlobalWorldState;
            provider.CreateAccount(TestItem.AddressA, 1.Ether());
            provider.Commit(MuirGlacier.Instance);
            provider.CommitTree(0);

            TrieStatsCollector visitor = new(new MemDb(), LimboLogs.Instance);
            worldStateManager.GlobalStateReader.RunTreeVisitor(visitor, provider.StateRoot);
        }
    }
}
