// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
