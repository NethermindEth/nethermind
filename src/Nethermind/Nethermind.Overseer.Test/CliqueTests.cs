using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Data;
using Nethermind.Overseer.Test.Framework;
using NUnit.Framework;

namespace Nethermind.Overseer.Test
{
    public class CliqueTests : TestBuilder
    {
        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public async Task One_validator()
        {
            StartCliqueMiner("val1")
                .Wait(5000)
                .Kill("val1");

            await ScenarioCompletion;
        }

        [Test]
        public async Task Two_validators()
        {
            StartCliqueMiner("val1")
                .StartCliqueMiner("val2")
                .Wait(10000)
                .Kill("val1")
                .Kill("val2");

            await ScenarioCompletion;
        }
        
        [Test]
        public async Task Clique_vote()
        {
            StartCliqueMiner("val1")
                .StartCliqueMiner("val2")
                .StartCliqueMiner("val3")
                .StartCliqueMiner("val4")
                .StartCliqueMiner("val5")
                .StartCliqueMiner("val6")
                .StartCliqueMiner("val7")
                .StartCliqueNode("node1")
                .SetContext(new CliqueContext(new CliqueState()))
                .Wait(20000)
                .SwitchNode("val1")
                .Propose(Nodes["node1"].Address, true)
                .SwitchNode("val2")
                .Propose(Nodes["node1"].Address, true)
                .SwitchNode("val5")
                .Propose(Nodes["node1"].Address, true)
                .SwitchNode("val7")
                .Propose(Nodes["node1"].Address, true)
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

            StartCliqueMiner("val1")
                .StartCliqueMiner("val2")
                .StartCliqueNode("node3")
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