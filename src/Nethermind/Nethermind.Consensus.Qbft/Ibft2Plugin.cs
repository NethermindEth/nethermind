// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.Qbft;

public class Ibft2Plugin(ChainSpec chainSpec) : IConsensusPlugin
{
    public string Name => SealEngineType;
    public string Description => "IBFT 2.0 Consensus Engine (follow only)";
    public string Author => "Nethermind";
    public bool Enabled => chainSpec.SealEngineType == SealEngineType;
    public string SealEngineType => Core.SealEngineType.Ibft2;
    public IModule Module => new Ibft2Module();
}
