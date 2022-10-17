//  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.JsonRpc.Data;
using Nethermind.Overseer.Test.Framework;
using NUnit.Framework;

namespace Nethermind.Overseer.Test
{
    [Explicit]
    public class CliqueTests : TestBuilder
    {
        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public async Task One_validator()
        {
            StartCliqueMiner("cliqueval1a")
                .Wait(5000)
                .Kill("cliqueval1a");

            await ScenarioCompletion;
        }

        [Test]
        public async Task Two_validators()
        {
            StartCliqueMiner("cliqueval1b")
                .StartCliqueMiner("cliqueval2b")
                .Wait(10000)
                .Kill("cliqueval1b")
                .Kill("cliqueval2b");

            await ScenarioCompletion;
        }

        [Test]
        public async Task Clique_vote()
        {
            StartCliqueMiner("cliqueval1c")
                .StartCliqueMiner("cliqueval2c")
                .StartCliqueMiner("cliqueval3c")
                .StartCliqueMiner("cliqueval4c")
                .StartCliqueMiner("cliqueval5c")
                .StartCliqueMiner("cliqueval6c")
                .StartCliqueMiner("cliqueval7c")
                .StartCliqueNode("cliquenode1c")
                .SetContext(new CliqueContext(new CliqueState()))
                .Wait(20000)
                .SwitchNode("cliqueval1c")
                .Propose(Nodes["cliquenode1c"].Address, true)
                .SwitchNode("cliqueval2c")
                .Propose(Nodes["cliquenode1c"].Address, true)
                .SwitchNode("cliqueval5c")
                .Propose(Nodes["cliquenode1c"].Address, true)
                .SwitchNode("cliqueval7c")
                .Propose(Nodes["cliquenode1c"].Address, true)
                .Wait(10000)
                .LeaveContext()
                .KillAll();

            await ScenarioCompletion;
        }

        [Test]
        public async Task Clique_transaction_broadcast()
        {
            TransactionForRpc tx = new TransactionForRpc();
            tx.Value = 2.Ether();
            tx.GasPrice = 20.GWei();
            tx.Gas = 21000;
            tx.From = new PrivateKey(new byte[32] {
                0,0,0,0,0,0,0,0,
                0,0,0,0,0,0,0,0,
                0,0,0,0,0,0,0,0,
                0,0,0,0,0,0,0,3
            }).Address;

            tx.To = new PrivateKey(new byte[32] {
                0,0,0,0,0,0,0,0,
                0,0,0,0,0,0,0,0,
                0,0,0,0,0,0,0,0,
                0,0,0,0,0,0,0,1
            }).Address;

            StartCliqueMiner("cliqueval1d")
                .StartCliqueMiner("cliqueval2d")
                .StartCliqueNode("cliquenode3d")
                .SetContext(new CliqueContext(new CliqueState()))
                .Wait(5000)
                .SendTransaction(tx)
                .Wait(10000)
                .LeaveContext()
                .KillAll();

            await ScenarioCompletion;
        }
    }
}
