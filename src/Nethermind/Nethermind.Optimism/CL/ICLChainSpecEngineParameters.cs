// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Optimism.CL;

public class CLChainSpecEngineParameters : IChainSpecEngineParameters
{
    public Address? BatcherInboxAddress { get; set; }
    public Address? BatcherAddress { get; set; }
    public Address? SequencerP2PAddress { get; set; }
    public string[]? Nodes { get; set; }
    public string? EngineName => "OptimismCL";
    public string? SealEngineType => null;
}
