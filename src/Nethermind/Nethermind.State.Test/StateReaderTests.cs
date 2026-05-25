// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Threading.Tasks;
using Autofac;
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
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [TestFixture("Standard")]
    [TestFixture("Flat")]
    [Parallelizable(ParallelScope.All)]
    public class StateReaderTests(string backend)
    {
        private const string Standard = "Standard";
        private const string Flat = "Flat";

        private static readonly Hash256 Hash1 = Keccak.Compute("1");
        private readonly Address _address1 = new(Hash1);
        private static readonly ILogManager Logger = LimboLogs.Instance;

        private sealed class Context : IDisposable
        {
            public IWorldState WorldState { get; }
            public IStateReader Reader { get; }
            private readonly IContainer? _container;

            public Context(string backend)
            {
                switch (backend)
                {
                    case Flat:
                        (WorldState, Reader, _container) = TestWorldStateFactory.CreateFlatForTestWithStateReader();
                        break;
                    case Standard:
                        (WorldState, Reader) = TestWorldStateFactory.CreateForTestWithStateReader();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(backend), backend, null);
                }
            }

            public BlockHeader CommitAndCapture(Action<IWorldState> populate, long blockNumber = 0)
            {
                using IDisposable _ = WorldState.BeginScope(IWorldState.PreGenesis);
                populate(WorldState);
                WorldState.Commit(MuirGlacier.Instance);
                WorldState.CommitTree(blockNumber);
                return Build.A.BlockHeader.WithNumber(blockNumber).WithStateRoot(WorldState.StateRoot).TestObject;
            }

            public void Dispose() => _container?.Dispose();
        }

        [Test]
        public void Can_ask_about_balance_in_parallel()
        {
            IReleaseSpec spec = MainnetSpecProvider.Instance.GetSpec((ForkActivation)MainnetSpecProvider.ConstantinopleFixBlockNumber);
            using Context ctx = new(backend);
            IWorldState provider = ctx.WorldState;
            IStateReader reader = ctx.Reader;

            using IDisposable _ = provider.BeginScope(IWorldState.PreGenesis);

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

            Task a = StartTask(reader, baseBlock0, 1);
            Task b = StartTask(reader, baseBlock1, 2);
            Task c = StartTask(reader, baseBlock2, 3);
            Task d = StartTask(reader, baseBlock3, 4);

            Task.WhenAll(a, b, c, d).Wait();
        }

        [Test]
        public void Can_ask_about_storage_in_parallel()
        {
            StorageCell storageCell = new(_address1, UInt256.One);
            IReleaseSpec spec = MuirGlacier.Instance;
            using Context ctx = new(backend);
            IWorldState provider = ctx.WorldState;
            IStateReader reader = ctx.Reader;
            using IDisposable _ = provider.BeginScope(IWorldState.PreGenesis);

            void UpdateStorageValue(byte[] newValue) => provider.Set(storageCell, newValue);

            void AddOneToBalance() => provider.AddToBalance(_address1, 1, spec);

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

            Task a = StartStorageTask(reader, baseBlock0, storageCell, new byte[] { 1 });
            Task b = StartStorageTask(reader, baseBlock1, storageCell, new byte[] { 2 });
            Task c = StartStorageTask(reader, baseBlock2, storageCell, new byte[] { 3 });
            Task d = StartStorageTask(reader, baseBlock3, storageCell, new byte[] { 4 });

            Task.WhenAll(a, b, c, d).Wait();
        }

        [Test]
        public void Non_existing()
        {
            StorageCell storageCell = new(_address1, UInt256.One);
            IReleaseSpec spec = MuirGlacier.Instance;

            using Context ctx = new(backend);
            IWorldState provider = ctx.WorldState;
            IStateReader reader = ctx.Reader;
            using IDisposable _ = provider.BeginScope(IWorldState.PreGenesis);

            void CommitEverything()
            {
                provider.Commit(spec);
                provider.CommitTree(0);
            }

            provider.CreateAccount(_address1, 1);
            provider.Set(storageCell, new byte[] { 1 });
            CommitEverything();
            Hash256 stateRoot0 = provider.StateRoot;

            reader.GetStorage(Build.A.BlockHeader.WithStateRoot(stateRoot0).TestObject, _address1, storageCell.Index + 1).ToArray().Should().BeEquivalentTo(new byte[] { 0 });
        }

        private Task StartTask(IStateReader reader, BlockHeader baseBlock, UInt256 value) => Task.Run(
                () =>
                {
                    for (int i = 0; i < 10000; i++)
                    {
                        UInt256 balance = reader.GetBalance(baseBlock, _address1);
                        Assert.That(balance, Is.EqualTo(value));
                    }
                });

        private Task StartStorageTask(IStateReader reader, BlockHeader baseBlock, StorageCell storageCell, byte[] value) => Task.Run(
                () =>
                {
                    for (int i = 0; i < 1000; i++)
                    {
                        byte[] result = reader.GetStorage(baseBlock, storageCell.Address, storageCell.Index).ToArray();
                        result.Should().BeEquivalentTo(value);
                    }
                });

        [Test]
        public void Get_storage()
        {
            /* all testing will be touching just a single storage cell */
            StorageCell storageCell = new(_address1, UInt256.One);

            using Context ctx = new(backend);
            IWorldState state = ctx.WorldState;
            IStateReader reader = ctx.Reader;
            byte[] initialValue = new byte[] { 1, 2, 3 };
            BlockHeader baseBlock;
            using (IDisposable _ = state.BeginScope(IWorldState.PreGenesis))
            {
                /* to start with we need to create an account that we will be setting storage at */
                state.CreateAccount(storageCell.Address, UInt256.One);
                state.Commit(MuirGlacier.Instance);
                state.CommitTree(1);

                /* at this stage we have an account with empty storage at the address that we want to test */

                state.Set(storageCell, initialValue);
                state.Commit(MuirGlacier.Instance);
                state.CommitTree(2);
                baseBlock = Build.A.BlockHeader.WithNumber(2).WithStateRoot(state.StateRoot).TestObject;
            }

            byte[] retrieved = reader.GetStorage(baseBlock, _address1, storageCell.Index).ToArray();
            retrieved.Should().BeEquivalentTo(initialValue);

            /* at this stage we set the value in storage to 1,2,3 at the tested storage cell */

            /* Now we are testing scenario where the storage is being changed by the block processor.
               To do that we create some different storage / state access stack that represents the processor.
               It is a different stack of objects than the one that is used by the blockchain bridge. */
            // Note: There is only one global IWorldState and IStateReader now. With pruning trie store, the data is
            // not written to db immediately.

            byte[] newValue = new byte[] { 1, 2, 3, 4, 5 };

            IWorldState processorStateProvider = state; // They are the same

            using (IDisposable _ = processorStateProvider.BeginScope(baseBlock))
            {
                processorStateProvider.Set(storageCell, newValue);
                processorStateProvider.Commit(MuirGlacier.Instance);
                processorStateProvider.CommitTree(baseBlock.Number + 1);
                baseBlock = Build.A.BlockHeader.WithParent(baseBlock).WithStateRoot(state.StateRoot).TestObject;
            }

            /* At this stage the DB should have the storage value updated to 5.
               We will try to retrieve the value by taking the state root from the processor.*/

            retrieved = reader.GetStorage(baseBlock, storageCell.Address, storageCell.Index).ToArray();
            retrieved.Should().BeEquivalentTo(newValue);

            /* If it failed then it means that the blockchain bridge cached the previous call value */
        }


        [Test]
        public void Can_collect_stats()
        {
            using Context ctx = new(backend);
            IWorldState provider = ctx.WorldState;
            IStateReader stateReader = ctx.Reader;

            Hash256 stateRoot;
            using (IDisposable _ = provider.BeginScope(IWorldState.PreGenesis))
            {
                provider.CreateAccount(TestItem.AddressA, 1.Ether);
                provider.Commit(MuirGlacier.Instance);
                provider.CommitTree(0);
                stateRoot = provider.StateRoot;
            }

            TrieStats stats = stateReader.CollectStats(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(0).TestObject, new MemDb(), Logger);
            stats.AccountCount.Should().Be(1);
        }

        private (Context ctx, IReleaseSpec releaseSpec, IDisposable scope) SetupContractSenderTest(
            bool eip3607Enabled, bool eip7702Enabled, byte[]? code = null)
        {
            IReleaseSpec releaseSpec = ReleaseSpecSubstitute.Create();
            releaseSpec.IsEip3607Enabled.Returns(eip3607Enabled);
            releaseSpec.IsEip7702Enabled.Returns(eip7702Enabled);
            Context ctx = new(backend);
            IWorldState sut = ctx.WorldState;
            IDisposable scope = sut.BeginScope(IWorldState.PreGenesis);
            sut.CreateAccount(TestItem.AddressA, 0);
            if (code is not null)
            {
                sut.InsertCode(TestItem.AddressA, ValueKeccak.Compute(code), code, releaseSpec, false);
            }
            sut.Commit(MuirGlacier.Instance);
            sut.CommitTree(0);
            return (ctx, releaseSpec, scope);
        }

        [TestCase(true, true, null, false, Description = "No code returns false")]
        [TestCase(true, true, new byte[] { 1 }, true, Description = "Has code returns true")]
        [TestCase(true, false, null, false, Description = "No code, 7702 disabled returns false")]
        public void IsInvalidContractSender_BasicCases(bool eip3607, bool eip7702, byte[]? code, bool expected)
        {
            (Context ctx, IReleaseSpec releaseSpec, IDisposable scope) = SetupContractSenderTest(eip3607, eip7702, code);
            using (ctx)
            using (scope)
            {
                bool result = ctx.WorldState.IsInvalidContractSender(releaseSpec, TestItem.AddressA);
                Assert.That(result, Is.EqualTo(expected));
            }
        }

        [Test]
        public void IsInvalidContractSender_AccountHasDelegatedCode_ReturnsFalse()
        {
            byte[] code = [.. Eip7702Constants.DelegationHeader, .. new byte[20]];
            (Context ctx, IReleaseSpec releaseSpec, IDisposable scope) = SetupContractSenderTest(eip3607Enabled: true, eip7702Enabled: true, code);
            using (ctx)
            using (scope)
            {
                bool result = ctx.WorldState.IsInvalidContractSender(releaseSpec, TestItem.AddressA);
                Assert.That(result, Is.False);
            }
        }

        [Test]
        public void IsInvalidContractSender_AccountHasCodeButDelegateReturnsTrue_ReturnsFalse()
        {
            byte[] code = new byte[20];
            (Context ctx, IReleaseSpec releaseSpec, IDisposable scope) = SetupContractSenderTest(eip3607Enabled: true, eip7702Enabled: true, code);
            using (ctx)
            using (scope)
            {
                bool result = ctx.WorldState.IsInvalidContractSender(releaseSpec, TestItem.AddressA, static (_) => true);
                Assert.That(result, Is.False);
            }
        }

        [Test]
        public void IsInvalidContractSender_AccountHasDelegatedCodeBut7702IsNotEnabled_ReturnsTrue()
        {
            byte[] code = [.. Eip7702Constants.DelegationHeader, .. new byte[20]];
            (Context ctx, IReleaseSpec releaseSpec, IDisposable scope) = SetupContractSenderTest(eip3607Enabled: true, eip7702Enabled: false, code);
            using (ctx)
            using (scope)
            {
                bool result = ctx.WorldState.IsInvalidContractSender(releaseSpec, TestItem.AddressA);
                Assert.That(result, Is.True);
            }
        }

        [Test]
        public void IsInvalidContractSender_AccountHasDelegatedCodeBut3607IsNotEnabled_ReturnsFalse()
        {
            byte[] code = [.. Eip7702Constants.DelegationHeader, .. new byte[20]];
            (Context ctx, IReleaseSpec releaseSpec, IDisposable scope) = SetupContractSenderTest(eip3607Enabled: false, eip7702Enabled: true, code);
            using (ctx)
            using (scope)
            {
                bool result = ctx.WorldState.IsInvalidContractSender(releaseSpec, TestItem.AddressA);
                Assert.That(result, Is.False);
            }
        }

        [Test]
        public void Can_accepts_visitors()
        {
            using Context ctx = new(backend);
            IWorldState provider = ctx.WorldState;
            IStateReader reader = ctx.Reader;
            Hash256 stateRoot;
            using (IDisposable _ = provider.BeginScope(IWorldState.PreGenesis))
            {
                provider.CreateAccount(TestItem.AddressA, 1.Ether);
                provider.Commit(MuirGlacier.Instance);
                provider.CommitTree(0);
                stateRoot = provider.StateRoot;
            }

            TrieStatsCollector visitor = new(new MemDb(), LimboLogs.Instance);
            reader.RunTreeVisitor(visitor, Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(0).TestObject);
        }

        [Test]
        public void Can_dump_state()
        {
            using Context ctx = new(backend);
            IWorldState provider = ctx.WorldState;
            IStateReader reader = ctx.Reader;

            Hash256 stateRoot;
            using (IDisposable _ = provider.BeginScope(IWorldState.PreGenesis))
            {
                provider.CreateAccount(TestItem.AddressA, 1.Ether);
                provider.Commit(MuirGlacier.Instance);
                provider.CommitTree(0);
                stateRoot = provider.StateRoot;
            }

            string state = reader.DumpState(Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(0).TestObject);
            state.Should().NotBeEmpty();
        }

        [Test]
        public void TryGetAccount_ExistingAccount_ReturnsTrue()
        {
            using Context ctx = new(backend);
            Account expected = TestItem.GenerateIndexedAccount(7);
            BlockHeader header = ctx.CommitAndCapture(state => state.CreateAccount(TestItem.AddressA, expected.Balance, expected.Nonce));

            bool result = ctx.Reader.TryGetAccount(header, TestItem.AddressA, out AccountStruct account);

            result.Should().BeTrue();
            account.Balance.Should().Be(expected.Balance);
            account.Nonce.Should().Be(expected.Nonce);
        }

        [Test]
        public void TryGetAccount_NonExistentAccount_ReturnsFalse()
        {
            using Context ctx = new(backend);
            BlockHeader header = ctx.CommitAndCapture(state => state.CreateAccount(TestItem.AddressA, 1, 1));

            // The out parameter is intentionally not asserted: the IStateReader contract leaves it
            // undefined when result is false, and the two implementations diverge
            // (StateReader => AccountStruct.TotallyEmpty, FlatStateReader => default).
            bool result = ctx.Reader.TryGetAccount(header, TestItem.AddressB, out _);

            result.Should().BeFalse();
        }

        [Test]
        public void GetStorage_ExistingSlot_ReturnsValue()
        {
            using Context ctx = new(backend);
            StorageCell cell = new(TestItem.AddressA, (UInt256)42);
            byte[] value = [0xab, 0xcd];
            BlockHeader header = ctx.CommitAndCapture(state =>
            {
                state.CreateAccount(TestItem.AddressA, 1);
                state.Set(cell, value);
            });

            byte[] result = ctx.Reader.GetStorage(header, cell.Address, cell.Index).ToArray();

            result.Should().Equal(value);
        }

        [Test]
        public void GetCode_EmptyHash_ReturnsEmpty()
        {
            using Context ctx = new(backend);

            ctx.Reader.GetCode(Keccak.OfAnEmptyString).Should().BeEmpty();
            ctx.Reader.GetCode(Keccak.OfAnEmptyString.ValueHash256).Should().BeEmpty();
        }

        [Test]
        public void GetCode_KnownCode_ReturnsCode()
        {
            using Context ctx = new(backend);
            byte[] code = [0x60, 0x80];
            ValueHash256 codeHash = ValueKeccak.Compute(code);
            ctx.CommitAndCapture(state =>
            {
                state.CreateAccount(TestItem.AddressA, 1);
                state.InsertCode(TestItem.AddressA, codeHash, code, MuirGlacier.Instance);
            });

            ctx.Reader.GetCode((Hash256)codeHash).Should().Equal(code);
            ctx.Reader.GetCode(codeHash).Should().Equal(code);
        }

        [Test]
        public void HasStateForBlock_CommittedBlock_ReturnsTrue()
        {
            using Context ctx = new(backend);
            BlockHeader header = ctx.CommitAndCapture(state => state.CreateAccount(TestItem.AddressA, 1));

            ctx.Reader.HasStateForBlock(header).Should().BeTrue();
        }
    }
}
