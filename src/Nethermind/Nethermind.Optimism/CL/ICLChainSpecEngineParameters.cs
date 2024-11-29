// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Optimism.CL;

public class CLChainSpecEngineParameters : IChainSpecEngineParameters
{
    public string? SequencerPubkey { get; set; }
    public string? EngineName => "OptimismCL";
    public string? SealEngineType => null;
}
