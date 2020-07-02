﻿//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [TestFixture]
    public class StateReaderTests
    {
        private static readonly Keccak Hash1 = Keccak.Compute("1");
        private readonly Address _address1 = new Address(Hash1);
        private static readonly ILogManager Logger = LimboLogs.Instance;

        [Test]
        public async Task Can_ask_about_balance_in_parallel()
        {
            IReleaseSpec spec = MainnetSpecProvider.Instance.GetSpec(MainnetSpecProvider.ConstantinopleFixBlockNumber);
            StateDb stateDb = new StateDb(new MemDb());
            StateProvider provider = new StateProvider(stateDb, Substitute.For<IDb>(), Logger);
            provider.CreateAccount(_address1, 0);
            provider.AddToBalance(_address1, 1, spec);
            provider.Commit(spec);
            provider.CommitTree();
            Keccak stateRoot0 = provider.StateRoot;

            provider.AddToBalance(_address1, 1, spec);
            provider.Commit(spec);
            provider.CommitTree();
            Keccak stateRoot1 = provider.StateRoot;

            provider.AddToBalance(_address1, 1, spec);
            provider.Commit(spec);
            provider.CommitTree();
            Keccak stateRoot2 = provider.StateRoot;

            provider.AddToBalance(_address1, 1, spec);
            provider.Commit(spec);
            provider.CommitTree();
            Keccak stateRoot3 = provider.StateRoot;

            provider.CommitTree();
            stateDb.Commit();

            StateReader reader = new StateReader(stateDb, Substitute.For<IDb>(), Logger);

            Task a = StartTask(reader, stateRoot0, 1);
            Task b = StartTask(reader, stateRoot1, 2);
            Task c = StartTask(reader, stateRoot2, 3);
            Task d = StartTask(reader, stateRoot3, 4);

            await Task.WhenAll(a, b, c, d);
        }

        [Test]
        public async Task Can_ask_about_storage_in_parallel()
        {
            StorageCell storageCell = new StorageCell(_address1, UInt256.One);
            IReleaseSpec spec = MuirGlacier.Instance;
            StateDb stateDb = new StateDb(new MemDb());
            StateProvider provider = new StateProvider(stateDb, new MemDb(), Logger);
            StorageProvider storageProvider = new StorageProvider(stateDb, provider, Logger);

            void UpdateStorageValue(byte[] newValue)
            {
                storageProvider.Set(storageCell, newValue);
            }

            void AddOneToBalance()
            {
                provider.AddToBalance(_address1, 1, spec);
            }

            void CommitEverything()
            {
                storageProvider.Commit();
                storageProvider.CommitTrees();
                provider.Commit(spec);
                provider.CommitTree();
            }

            provider.CreateAccount(_address1, 1);
            CommitEverything();

            AddOneToBalance();
            UpdateStorageValue(new byte[] {1});
            CommitEverything();
            Keccak stateRoot0 = provider.StateRoot;

            AddOneToBalance();
            UpdateStorageValue(new byte[] {2});
            CommitEverything();
            Keccak stateRoot1 = provider.StateRoot;

            AddOneToBalance();
            UpdateStorageValue(new byte[] {3});
            CommitEverything();
            Keccak stateRoot2 = provider.StateRoot;

            AddOneToBalance();
            UpdateStorageValue(new byte[] {4});
            CommitEverything();
            Keccak stateRoot3 = provider.StateRoot;

            stateDb.Commit();

            StateReader reader = new StateReader(stateDb, Substitute.For<IDb>(), Logger);

            Task a = StartStorageTask(reader, stateRoot0, storageCell, new byte[] {1});
            Task b = StartStorageTask(reader, stateRoot1, storageCell, new byte[] {2});
            Task c = StartStorageTask(reader, stateRoot2, storageCell, new byte[] {3});
            Task d = StartStorageTask(reader, stateRoot3, storageCell, new byte[] {4});

            await Task.WhenAll(a, b, c, d);
        }

        [Test]
        public void Non_existing()
        {
            StorageCell storageCell = new StorageCell(_address1, UInt256.One);
            IReleaseSpec spec = MuirGlacier.Instance;
            StateDb stateDb = new StateDb(new MemDb());
            StateProvider provider = new StateProvider(stateDb, new MemDb(), Logger);
            StorageProvider storageProvider = new StorageProvider(stateDb, provider, Logger);

            void CommitEverything()
            {
                storageProvider.Commit();
                storageProvider.CommitTrees();
                provider.Commit(spec);
                provider.CommitTree();
            }

            provider.CreateAccount(_address1, 1);
            storageProvider.Set(storageCell, new byte[] {1});
            CommitEverything();
            Keccak stateRoot0 = provider.StateRoot;

            stateDb.Commit();
            StateReader reader = new StateReader(stateDb, Substitute.For<IDb>(), Logger);
            Keccak storageRoot = reader.GetStorageRoot(stateRoot0, _address1);
            reader.GetStorage(storageRoot, storageCell.Index + 1).Should().BeEquivalentTo(new byte[] {0});
            reader.GetStorage(Keccak.EmptyTreeHash, storageCell.Index + 1).Should().BeEquivalentTo(new byte[] {0});
        }

        private Task StartTask(StateReader reader, Keccak stateRoot, UInt256 value)
        {
            return Task.Run(
                () =>
                {
                    for (int i = 0; i < 100000; i++)
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
                    for (int i = 0; i < 10000; i++)
                    {
                        Keccak storageRoot = reader.GetStorageRoot(stateRoot, storageCell.Address);
                        byte[] result = reader.GetStorage(storageRoot, storageCell.Index);
                        result.Should().BeEquivalentTo(value);
                    }
                });
        }
    }
}