// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Network.Contract.P2P;
using Nethermind.Specs.ChainSpecStyle;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

public class XdcPlugin(ChainSpec chainSpec) : IConsensusPlugin
{
    private INethermindApi _nethermindApi;
    public const string Xdc = "Xdc";
    public string Author => "Nethermind";
    public string Name => Xdc;
    public string Description => "Xdc support for Nethermind";
    public bool Enabled => chainSpec.SealEngineType == SealEngineType;
    public string SealEngineType => XdcConstants.XDPoS;
    public IModule Module => new XdcModule();

    public Task Init(INethermindApi nethermindApi)
    {
        _nethermindApi = nethermindApi;
        return Task.CompletedTask;
    }

    public Task InitNetworkProtocol()
    {
        // Remove default ETH 68 capability (XDC uses 62-63 and 100)
        _nethermindApi.ProtocolsManager!.RemoveSupportedCapability(new(Protocol.Eth, 68));

        _nethermindApi.ProtocolsManager!.AddSupportedCapability(new(Protocol.Eth, 62));
        _nethermindApi.ProtocolsManager!.AddSupportedCapability(new(Protocol.Eth, 63));
        _nethermindApi.ProtocolsManager!.AddSupportedCapability(new(Protocol.Eth, 100));
        return Task.CompletedTask;
    }
}
