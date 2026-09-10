// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.Qbft;

/// <summary>Besu-compatible QBFT proof-of-authority consensus.</summary>
public class QbftPlugin(ChainSpec chainSpec) : IConsensusPlugin
{
    public string Name => SealEngineType;
    public string Description => "QBFT Consensus Engine";
    public string Author => "Nethermind";
    public bool Enabled => chainSpec.SealEngineType == SealEngineType;
    public string SealEngineType => Core.SealEngineType.Qbft;
    public IModule Module => new QbftModule();
}
