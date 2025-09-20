// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Api.Extensions;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Autofac;
using Autofac.Core;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc;

public class XdcPlugin(ChainSpec chainSpec) : IConsensusPlugin
{
    public const string Xdc = "Xdc";
    public string Author => "Nethermind";
    public string Name => Xdc;
    public string Description => "Xdc support for Nethermind";
    public bool Enabled => chainSpec.SealEngineType == SealEngineType;
    public bool MustInitialize => true;
    public string SealEngineType => Core.SealEngineType.XDPoS;
    public IModule Module => new XdcModule();

    // IConsensusPlugin
    public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer _)
    {
        throw new NotSupportedException();
    }
    public IBlockProducer InitBlockProducer()
    {
        throw new NotSupportedException();
    }
}

public class XdcModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<ISpecProvider, XdcChainSpecBasedSpecProvider>()
            .Map<XdcChainSpecEngineParameters, ChainSpec>(chainSpec =>
                chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<XdcChainSpecEngineParameters>())

            // Validators
            .AddSingleton<IHeaderValidator, XdcHeaderValidator>()

            ;
    }

}
