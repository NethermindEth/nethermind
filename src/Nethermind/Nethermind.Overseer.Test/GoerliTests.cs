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
using Nethermind.Overseer.Test.Framework;
using NUnit.Framework;

namespace Nethermind.Overseer.Test
{
    [Explicit]
    public class GoerliTests : TestBuilder
    {
        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public async Task Goerli_initial_voting()
        {
            StartGoerliMiner("val1")
                .StartGoerliNode("val2")
                .StartGoerliNode("val3")
                .StartGoerliNode("val4")
                .StartGoerliNode("val5")
                .StartGoerliNode("val6")
                .StartGoerliNode("val7")
                .SetContext(new CliqueContext(new CliqueState()))
                .Wait(10000)
                .SwitchNode("val1")
                .Propose(Nodes["val2"].Address, true)
                .Wait(10000)
                .SwitchNode("val1")
                .Propose(Nodes["val3"].Address, true)
                .SwitchNode("val2")
                .Propose(Nodes["val3"].Address, true)
                .Wait(10000)
                .SwitchNode("val1")
                .Propose(Nodes["val4"].Address, true)
                .SwitchNode("val2")
                .Propose(Nodes["val4"].Address, true)
                .SwitchNode("val3")
                .Propose(Nodes["val4"].Address, true)
                .Wait(10000)
                .SwitchNode("val1")
                .Propose(Nodes["val5"].Address, true)
                .SwitchNode("val2")
                .Propose(Nodes["val5"].Address, true)
                .SwitchNode("val3")
                .Propose(Nodes["val5"].Address, true)
                .SwitchNode("val4")
                .Propose(Nodes["val5"].Address, true)
                .Wait(10000)
                .SwitchNode("val1")
                .Propose(Nodes["val6"].Address, true)
                .SwitchNode("val2")
                .Propose(Nodes["val6"].Address, true)
                .SwitchNode("val3")
                .Propose(Nodes["val6"].Address, true)
                .SwitchNode("val4")
                .Propose(Nodes["val6"].Address, true)
                .SwitchNode("val5")
                .Propose(Nodes["val6"].Address, true)
                .Wait(10000)
                .LeaveContext()
                .KillAll();

            await ScenarioCompletion;
        }
    }
}
