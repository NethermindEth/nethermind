/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
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
            IReleaseSpec spec = MainNetSpecProvider.Instance.GetSpec(MainNetSpecProvider.ConstantinopleFixBlockNumber);
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
    }
}