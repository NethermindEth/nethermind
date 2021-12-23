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

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.Runner.Ethereum.Api;
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
        public async Task Proof_module_is_registered_if_configured()
        {
            JsonRpcConfig jsonRpcConfig = new() {Enabled = true};

            NethermindApi context = Build.ContextWithMocks();
            context.ConfigProvider.GetConfig<IJsonRpcConfig>().Returns(jsonRpcConfig);
            
            RegisterRpcModules registerRpcModules = new(context);
            await registerRpcModules.Execute(CancellationToken.None);

            context.RpcModuleProvider.Check("proof_call", new JsonRpcContext(RpcEndpoint.Http)).Should().Be(ModuleResolution.Enabled);
        }
        
        [Test]
        public async Task Proof_module_is_not_registered_when_json_rpc_not_enabled()
        {
            JsonRpcConfig jsonRpcConfig = new() {Enabled = false};

            NethermindApi context = Build.ContextWithMocks();
            context.ConfigProvider.GetConfig<IJsonRpcConfig>().Returns(jsonRpcConfig);
            context.RpcModuleProvider.Enabled.Returns(Array.Empty<string>());

            RegisterRpcModules registerRpcModules = new(context);
            await registerRpcModules.Execute(CancellationToken.None);
            
            context.RpcModuleProvider.DidNotReceiveWithAnyArgs().Register<IProofRpcModule>(null);
        }
    }
}
