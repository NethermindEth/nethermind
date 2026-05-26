// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Consensus;
using Nethermind.Specs.ChainSpecStyle;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

public class XdcSubnetPlugin(ChainSpec chainSpec) : IConsensusPlugin
{
    private INethermindApi _nethermindApi;
    private IConsensusPlugin _xdcPlugin = new XdcPlugin(chainSpec);

    public const string XdcSubnet = "XdcSubnet";
    public string Author => "Nethermind";
    public string Name => XdcSubnet;
    public string Description => "Xdc subnet support for Nethermind";
    public bool Enabled => chainSpec.SealEngineType == SealEngineType;
    public string SealEngineType => XdcConstants.XDPoSSubnet;
    public IModule Module => new XdcSubnetModule();

    public Task Init(INethermindApi nethermindApi)
    {
        _nethermindApi = nethermindApi;
        return _xdcPlugin.Init(nethermindApi);
    }

    public Task InitNetworkProtocol() => _xdcPlugin.InitNetworkProtocol();

    // IConsensusPlugin
    public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer) => _xdcPlugin.InitBlockProducerRunner(blockProducer);
    public IBlockProducer InitBlockProducer()
    {
        StartXdcSubnetBlockProducer start = _nethermindApi.Context.Resolve<StartXdcSubnetBlockProducer>();
        return start.BuildProducer();
    }
}
