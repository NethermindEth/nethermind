// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Xdc;

public class XdcSubnetPlugin(ChainSpec chainSpec) : IConsensusPlugin
{
    private readonly XdcPlugin _xdcPlugin = new(chainSpec);

    public const string XdcSubnet = "XdcSubnet";
    public string Author => "Nethermind";
    public string Name => XdcSubnet;
    public string Description => "Xdc subnet support for Nethermind";
    public bool Enabled => chainSpec.SealEngineType == SealEngineType;
    public string SealEngineType => XdcConstants.XDPoSSubnet;
    public IModule Module => new XdcSubnetModule();

}
