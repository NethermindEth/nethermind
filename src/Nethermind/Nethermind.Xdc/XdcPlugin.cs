// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Specs.ChainSpecStyle;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

public class XdcPlugin(ChainSpec chainSpec) : IConsensusPlugin
{
    public const string Xdc = "Xdc";
    public string Author => "Nethermind";
    public string Name => Xdc;
    public string Description => "Xdc support for Nethermind";
    public bool Enabled => chainSpec.SealEngineType == SealEngineType;
    public string SealEngineType => XdcConstants.XDPoS;
    public IModule Module => new XdcModule();

    public Task Init(INethermindApi nethermindApi) => Task.CompletedTask;
}
