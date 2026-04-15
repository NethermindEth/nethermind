// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Taiko.TaikoSpec;

public class TaikoChainSpecEngineParameters : IChainSpecEngineParameters
{
    public string EngineName => SealEngineType;
    public string SealEngineType => Core.SealEngineType.Taiko;
    public long? OntakeTransition { get; set; }
    public long? PacayaTransition { get; set; }
    public ulong? ShastaTimestamp { get; set; }
    public bool? UseSurgeGasPriceOracle { get; set; }
    public ulong? Rip7728TransitionTimestamp { get; set; }
    public ulong? L1StaticCallTransitionTimestamp { get; set; }

    public Address TaikoL2Address { get; set; } = new("0x1670000000000000000000000000000000010001");

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

        if (ShastaTimestamp is not null)
        {
            timestamps.Add(ShastaTimestamp.Value);
        }

        if (Rip7728TransitionTimestamp is not null)
        {
            timestamps.Add(Rip7728TransitionTimestamp.Value);
        }

        if (L1StaticCallTransitionTimestamp is not null)
        {
            timestamps.Add(L1StaticCallTransitionTimestamp.Value);
        }
    }
}
