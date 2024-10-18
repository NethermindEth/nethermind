// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Microsoft.FSharp.Core;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.AuRa.Test
{
    public class AuRaPluginTests
    {
        [Test]
        public void Init_when_not_AuRa_doesnt_trow()
        {
            ChainSpec chainSpec = new ChainSpec()
            {
                SealEngineType = SealEngineType.AuRa
            };
            AuRaPlugin auRaPlugin = new(chainSpec);

            NethermindApi api = Runner.Test.Ethereum.Build.ContextWithMocks(containerConfigurer: (builder) =>
            {
                builder.AddModule(auRaPlugin.ContainerModule!);
            });

            Action init = () => auRaPlugin.Init(api);
            init.Should().NotThrow();
        }
    }
}
