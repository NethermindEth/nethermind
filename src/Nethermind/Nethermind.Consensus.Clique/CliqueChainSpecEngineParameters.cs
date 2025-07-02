// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.Clique;

public class CliqueChainSpecEngineParameters : IChainSpecEngineParameters
{
    public string? EngineName => SealEngineType;
    public string? SealEngineType => Core.SealEngineType.Clique;
    public ulong Epoch { get; set; }
    public ulong Period { get; set; }
    public UInt256? Reward { get; set; } = UInt256.Zero;
}
