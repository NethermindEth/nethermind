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
using Nethermind.Core.Specs;
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
            NethermindApi.Dependencies apiDependencies = new NethermindApi.Dependencies(
                new ConfigProvider(),
                new EthereumJsonSerializer(),
                new TestLogManager(),
                chainSpec,
                Substitute.For<ISpecProvider>(),
                [],
                Substitute.For<IProcessExitSource>(),
                Substitute.For<IContainer>());
            Action init = () => auRaPlugin.Init(new AuRaNethermindApi(apiDependencies));
            init.Should().NotThrow();
        }

    }
}
