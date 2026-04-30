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
    public ulong? UnzenTimestamp { get; set; }
    public bool? UseSurgeGasPriceOracle { get; set; }
    public ulong? Rip7728TransitionTimestamp { get; set; }
    public ulong? L1StaticCallTransitionTimestamp { get; set; }

    public Address TaikoL2Address { get; set; } = new("0x1670000000000000000000000000000000010001");

    public void AddTransitions(SortedSet<long> blockNumbers, SortedSet<ulong> timestamps, ulong genesisTimestamp = 0)
    {
        if (OntakeTransition is { } o && o > 0)
        {
            blockNumbers.Add(o);
        }

        if (PacayaTransition is { } p && p > 0)
        {
            blockNumbers.Add(p);
        }

        if (ShastaTimestamp is { } s && s > genesisTimestamp)
        {
            timestamps.Add(s);
        }

        if (Rip7728TransitionTimestamp is { } r && r > genesisTimestamp)
        {
            timestamps.Add(r);
        }

        if (L1StaticCallTransitionTimestamp is { } l && l > genesisTimestamp)
        {
            timestamps.Add(l);
        }

        if (UnzenTimestamp is { } u && u > genesisTimestamp)
        {
            timestamps.Add(u);
        }
    }
}
