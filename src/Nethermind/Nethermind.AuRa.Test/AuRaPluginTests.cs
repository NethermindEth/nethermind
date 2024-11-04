// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Config;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Test.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.AuRa.Test
{
    public class AuRaPluginTests
    {
        [Test]
        public void Init_when_not_AuRa_doesnt_trow()
        {
            AuRaPlugin auRaPlugin = new();
            ChainSpec chainSpec = new();
            chainSpec.EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(new AuRaChainSpecEngineParameters());
            Action init = () => auRaPlugin.Init(new AuRaNethermindApi(new ConfigProvider(), new EthereumJsonSerializer(), new TestLogManager(), chainSpec));
            init.Should().NotThrow();
        }

    }
}
