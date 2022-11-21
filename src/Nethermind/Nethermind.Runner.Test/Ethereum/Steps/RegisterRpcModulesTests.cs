// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            JsonRpcConfig jsonRpcConfig = new() { Enabled = true };

            NethermindApi context = Build.ContextWithMocks();
            context.ConfigProvider.GetConfig<IJsonRpcConfig>().Returns(jsonRpcConfig);

            RegisterRpcModules registerRpcModules = new(context);
            await registerRpcModules.Execute(CancellationToken.None);

            context.RpcModuleProvider.Check("proof_call", new JsonRpcContext(RpcEndpoint.Http)).Should().Be(ModuleResolution.Enabled);
        }

        [Test]
        public async Task Proof_module_is_not_registered_when_json_rpc_not_enabled()
        {
            JsonRpcConfig jsonRpcConfig = new() { Enabled = false };

            NethermindApi context = Build.ContextWithMocks();
            context.ConfigProvider.GetConfig<IJsonRpcConfig>().Returns(jsonRpcConfig);
            context.RpcModuleProvider.Enabled.Returns(Array.Empty<string>());

            RegisterRpcModules registerRpcModules = new(context);
            await registerRpcModules.Execute(CancellationToken.None);

            context.RpcModuleProvider.DidNotReceiveWithAnyArgs().Register<IProofRpcModule>(null);
        }
    }
}
