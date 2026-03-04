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
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Test.ChainSpecStyle;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test
{
    public class AuRaPluginTests
    {
        [Test]
        public void Init_when_not_AuRa_does_not_throw()
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
            Action init = () => auRaPlugin.Init(api);
            init.Should().NotThrow();
        }

        [Test]
        public void ApplyToReleaseSpec_sets_Eip158IgnoredAccount()
        {
            AuRaChainSpecEngineParameters parameters = new();
            ReleaseSpec spec = new();

            parameters.ApplyToReleaseSpec(spec, 0, null);

            spec.Eip158IgnoredAccount.Should().Be(Address.SystemUser);
        }

    }
}
