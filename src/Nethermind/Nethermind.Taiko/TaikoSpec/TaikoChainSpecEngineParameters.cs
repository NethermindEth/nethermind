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
    public ulong? UnzenBlockZkGasLimit { get; set; }
    public ulong? UnzenTxIntrinsicZkGas { get; set; }

    /// <summary>
    /// Ordered list of Unzen ZK gas multiplier schedules, each pinned to an activation timestamp.
    /// The schedule with the largest <see cref="TaikoUnzenZkGasSchedule.Timestamp"/> not exceeding
    /// the active block timestamp is in effect; if every entry activates in the future, the
    /// earliest acts as a floor so the meter always has a table.
    /// </summary>
    public List<TaikoUnzenZkGasSchedule>? UnzenZkGasSchedules { get; set; }

    public bool? UseSurgeGasPriceOracle { get; set; }
    public ulong? Rip7728TransitionTimestamp { get; set; }
    public ulong? L1StaticCallTransitionTimestamp { get; set; }

    public Address TaikoL2Address { get; set; } = new("0x1670000000000000000000000000000000010001");

    public void AddTransitions(SortedSet<long> blockNumbers, SortedSet<ulong> timestamps)
    {
        // Skip activations at timestamp 0: Taiko chainspecs use 0 to mean "active at genesis", and
        // EIP-2124 excludes pre-genesis activations from the fork-id walk. Folding a 0-timestamp
        // transition into the CRC32 walk produces a fork-id that diverges from taiko-geth /
        // alethia-reth peers and trips InvalidForkId disconnects.
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

        // Each schedule's activation timestamp is itself a consensus change, so it must be folded
        // into the EIP-2124 fork-id walk. The SortedSet dedups a schedule that activates exactly at
        // UnzenTimestamp. The MaxValue-1 placeholder used to register a default schedule that has
        // no scheduled real-world activation is filtered out the same way Shasta=0 / Unzen=0 are.
        if (UnzenZkGasSchedules is not null)
        {
            foreach (TaikoUnzenZkGasSchedule schedule in UnzenZkGasSchedules)
            {
                if (schedule.Timestamp > 0 && schedule.Timestamp < ulong.MaxValue - 1)
                {
                    timestamps.Add(schedule.Timestamp);
                }
            }
        }
    }
}
