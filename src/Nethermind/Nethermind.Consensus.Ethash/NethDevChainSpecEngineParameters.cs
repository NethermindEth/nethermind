// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.Ethash;

public class NethDevChainSpecEngineParameters : IChainSpecEngineParameters
{
    public string? EngineName => NethDevPlugin.NethDev;
    public string? SealEngineType => NethDevPlugin.NethDev;
}
