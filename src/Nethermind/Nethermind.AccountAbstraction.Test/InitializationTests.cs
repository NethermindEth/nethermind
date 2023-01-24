// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.JsonRpc;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AccountAbstraction.Test
{
    [TestFixture]
    internal class InitializationTests
    {
        private INethermindApi? _api;
        private AccountAbstractionPlugin? _accountAbstractionPlugin;

        [SetUp]
        public void Setup()
        {
            _api = Substitute.For<INethermindApi>();
            IAccountAbstractionConfig accountAbstractionConfig = Substitute.For<IAccountAbstractionConfig>();
            accountAbstractionConfig.Enabled.Returns(true);
            accountAbstractionConfig.EntryPointContractAddresses.Returns("0x0101010101010101010101010101010101010101");
            _api.Config<IAccountAbstractionConfig>().Returns(accountAbstractionConfig);
            _api.Config<IJsonRpcConfig>().Returns(Substitute.For<IJsonRpcConfig>());
            _api.ForRpc.Returns((Substitute.For<IApiWithNetwork>(), Substitute.For<INethermindApi>()));
            _api.ForProducer.Returns((Substitute.For<IApiWithBlockchain>(), Substitute.For<IApiWithBlockchain>()));

            _accountAbstractionPlugin = new();
            _accountAbstractionPlugin.Init(_api);
            _accountAbstractionPlugin.InitNetworkProtocol();
        }

        [Test]
        public void ChainId_is_used_for_UserOperationPool()
        {
            _ = _api!.SpecProvider!.Received().ChainId;
            _ = _api.SpecProvider!.DidNotReceive().NetworkId;
        }
    }
}
