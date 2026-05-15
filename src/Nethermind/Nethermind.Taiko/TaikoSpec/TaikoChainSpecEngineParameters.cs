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

    public void AddTransitions(SortedSet<long> blockNumbers, SortedSet<ulong> timestamps)
    {
        // Filter activations at or before genesis. Taiko chainspecs always have genesis
        // timestamp 0 (see taiko-alethia.json, taiko-hoodi.json, gavin's nmc-devnet.json),
        // so the EIP-2124 "skip activations <= genesis" rule simplifies to ">0". Without this
        // filter, Shasta=0 / Unzen=0 entries get folded into the CRC32 fork-id walk
        // unconditionally, producing a hash chain that diverges from taiko-geth/alethia-reth
        // peers (observed 0xbf99ee8f vs expected 0x7fec7e13 → InvalidForkId disconnect).
        if (OntakeTransition is { } o && o > 0)
        {
            blockNumbers.Add(o);
        }

        if (PacayaTransition is { } p && p > 0)
        {
            blockNumbers.Add(p);
        }

        if (ShastaTimestamp is { } s && s > 0)
        {
            timestamps.Add(s);
        }

        if (Rip7728TransitionTimestamp is { } r && r > 0)
        {
            timestamps.Add(r);
        }

        if (L1StaticCallTransitionTimestamp is { } l && l > 0)
        {
            timestamps.Add(l);
        }

        if (UnzenTimestamp is { } u && u > 0)
        {
            timestamps.Add(u);
        }
    }
}
