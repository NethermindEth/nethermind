//  Copyright (c) 2018 Demerzel Solutions Limited
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

using Nethermind.Config;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Runner.Ethereum.Steps;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Ethereum.Steps
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class RegisterRpcModulesTests
    {
        [Test]
        public void Proof_module_is_registered_if_configured()
        {
            JsonRpcConfig jsonRpcConfig = new JsonRpcConfig();
            jsonRpcConfig.Enabled = true;
            
            IConfigProvider configProvider = Substitute.For<IConfigProvider>();
            configProvider.GetConfig<IJsonRpcConfig>().Returns(jsonRpcConfig);

            IRpcModuleProvider rpcModuleProvider = Substitute.For<IRpcModuleProvider>();

            EthereumRunnerContext context = Build.ContextWithMocks();
            context.ConfigProvider = configProvider;
            context.RpcModuleProvider = rpcModuleProvider;
            
            RegisterRpcModules registerRpcModules = new RegisterRpcModules(context);
            registerRpcModules.Execute();
            
            rpcModuleProvider.ReceivedWithAnyArgs().Register<IProofModule>(null);
        }
        
        [Test]
        public void Proof_module_is_not_registered_when_json_rpc_not_enabled()
        {
            JsonRpcConfig jsonRpcConfig = new JsonRpcConfig();
            jsonRpcConfig.Enabled = false;
            
            IConfigProvider configProvider = Substitute.For<IConfigProvider>();
            configProvider.GetConfig<IJsonRpcConfig>().Returns(jsonRpcConfig);

            IRpcModuleProvider rpcModuleProvider = Substitute.For<IRpcModuleProvider>();

            EthereumRunnerContext context = new EthereumRunnerContext(configProvider, LimboLogs.Instance);
            context.ConfigProvider = configProvider;
            context.RpcModuleProvider = rpcModuleProvider;

            RegisterRpcModules registerRpcModules = new RegisterRpcModules(context);
            registerRpcModules.Execute();
            
            rpcModuleProvider.DidNotReceiveWithAnyArgs().Register<IProofModule>(null);
        }
    }
}