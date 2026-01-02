// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.JsonRpc.Modules.Evm;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class EvmModuleTests
    {
        [Test]
        public async Task Evm_mine()
        {
            IManualBlockProductionTrigger trigger = Substitute.For<IManualBlockProductionTrigger>();
            EvmRpcModule rpcModule = new(trigger);
            string response = await RpcTest.TestSerializedRequest<IEvmRpcModule>(rpcModule, "evm_mine");
            Assert.That(response, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":true,\"id\":67}"));
            await trigger.Received().BuildBlock();
        }
    }
}
