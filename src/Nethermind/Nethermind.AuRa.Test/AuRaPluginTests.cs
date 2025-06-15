// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Test.ChainSpecStyle;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test
{
    public class AuRaPluginTests
    {
        [Test]
        public void Init_when_not_AuRa_doesnt_trow()
        {
            ChainSpec chainSpec = new();
            AuRaPlugin auRaPlugin = new(chainSpec);
            chainSpec.EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(new AuRaChainSpecEngineParameters());
            using IContainer testNethermindContainer = new ContainerBuilder().AddModule(new TestNethermindModule()).Build();
            NethermindApi.Dependencies apiDependencies = new NethermindApi.Dependencies(
                new ConfigProvider(),
                new EthereumJsonSerializer(),
                new TestLogManager(),
                chainSpec,
                Substitute.For<ISpecProvider>(),
                [],
                Substitute.For<IProcessExitSource>(),
                testNethermindContainer);
            AuRaNethermindApi api = new AuRaNethermindApi(apiDependencies);
            api.DbProvider = TestMemDbProvider.Init();
            Action init = () => auRaPlugin.Init(api);
            init.Should().NotThrow();
        }

    }
}
