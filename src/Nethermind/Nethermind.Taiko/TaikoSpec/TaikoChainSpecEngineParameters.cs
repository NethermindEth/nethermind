// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Taiko.TaikoSpec;

public class TaikoChainSpecEngineParameters : IChainSpecEngineParameters
{
    public string EngineName => SealEngineType;
    public string SealEngineType => Core.SealEngineType.Taiko;
    public long? OntakeTransition { get; set; }
    public long? PacayaTransition { get; set; }
    public bool? UseSurgeGasPriceOracle { get; set; }
    public string[]? L1SloadRestrictedAddresses { get; set; }

    public void AddTransitions(SortedSet<long> blockNumbers, SortedSet<ulong> timestamps)
    {
        if (OntakeTransition is not null)
        {
            blockNumbers.Add(OntakeTransition.Value);
        }

        if (PacayaTransition is not null)
        {
            blockNumbers.Add(PacayaTransition.Value);
        }
    }
}
