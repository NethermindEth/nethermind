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
            StartGoerliMiner("goerlival1")
                .StartGoerliNode("goerlival2")
                .StartGoerliNode("goerlival3")
                .StartGoerliNode("goerlival4")
                .StartGoerliNode("goerlival5")
                .StartGoerliNode("goerlival6")
                .StartGoerliNode("goerlival7")
                .SetContext(new CliqueContext(new CliqueState()))
                .Wait(10000)
                .SwitchNode("goerlival1")
                .Propose(Nodes["goerlival2"].Address, true)
                .Wait(10000)
                .SwitchNode("goerlival1")
                .Propose(Nodes["goerlival3"].Address, true)
                .SwitchNode("goerlival2")
                .Propose(Nodes["goerlival3"].Address, true)
                .Wait(10000)
                .SwitchNode("goerlival1")
                .Propose(Nodes["goerlival4"].Address, true)
                .SwitchNode("goerlival2")
                .Propose(Nodes["goerlival4"].Address, true)
                .SwitchNode("goerlival3")
                .Propose(Nodes["goerlival4"].Address, true)
                .Wait(10000)
                .SwitchNode("goerlival1")
                .Propose(Nodes["goerlival5"].Address, true)
                .SwitchNode("goerlival2")
                .Propose(Nodes["goerlival5"].Address, true)
                .SwitchNode("goerlival3")
                .Propose(Nodes["goerlival5"].Address, true)
                .SwitchNode("goerlival4")
                .Propose(Nodes["goerlival5"].Address, true)
                .Wait(10000)
                .SwitchNode("goerlival1")
                .Propose(Nodes["goerlival6"].Address, true)
                .SwitchNode("goerlival2")
                .Propose(Nodes["goerlival6"].Address, true)
                .SwitchNode("goerlival3")
                .Propose(Nodes["goerlival6"].Address, true)
                .SwitchNode("goerlival4")
                .Propose(Nodes["goerlival6"].Address, true)
                .SwitchNode("goerlival5")
                .Propose(Nodes["goerlival6"].Address, true)
                .Wait(10000)
                .LeaveContext()
                .KillAll();

            await ScenarioCompletion;
        }
    }
}
