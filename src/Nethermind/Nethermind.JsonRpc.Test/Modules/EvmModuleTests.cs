// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        public void Evm_mine()
        {
            IManualBlockProductionTrigger trigger = Substitute.For<IManualBlockProductionTrigger>();
            EvmRpcModule rpcModule = new(trigger);
            string response = RpcTest.TestSerializedRequest<IEvmRpcModule>(rpcModule, "evm_mine");
            Assert.That(response, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":true,\"id\":67}"));
            trigger.Received().BuildBlock();
        }
    }
}
