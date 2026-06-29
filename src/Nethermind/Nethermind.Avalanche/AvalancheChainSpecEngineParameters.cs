// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Avalanche;

/// <summary>
/// Chainspec <c>engine.avalanche</c> parameters describing the Avalanche C-Chain (Coreth)
/// network-upgrade activation timestamps used to drive EVM hard-fork selection.
/// </summary>
/// <remarks>
/// Avalanche upgrades are activated by Unix timestamp. The set below mirrors Coreth's
/// historical upgrade sequence (Apricot phases through the most recent network upgrades).
/// The exact mapping of each upgrade to EVM EIP activation still needs to be completed in
/// <see cref="ApplyToReleaseSpec"/>.
/// </remarks>
public class AvalancheChainSpecEngineParameters : IChainSpecEngineParameters
{
    public string? EngineName => SealEngineType;
    public string? SealEngineType => Core.SealEngineType.Avalanche;

    public ulong? ApricotPhase1Timestamp { get; set; }
    public ulong? ApricotPhase2Timestamp { get; set; }
    public ulong? ApricotPhase3Timestamp { get; set; }
    public ulong? ApricotPhase4Timestamp { get; set; }
    public ulong? ApricotPhase5Timestamp { get; set; }
    public ulong? BanffTimestamp { get; set; }
    public ulong? CortinaTimestamp { get; set; }
    public ulong? DurangoTimestamp { get; set; }
    public ulong? EtnaTimestamp { get; set; }
    public ulong? FortunaTimestamp { get; set; }
    public ulong? GraniteTimestamp { get; set; }

    public void ApplyToReleaseSpec(ReleaseSpec spec, ulong startBlock, ulong? startTimestamp)
    {
        // TODO: map Avalanche C-Chain upgrades onto EVM EIP activation flags on the base ReleaseSpec.
        // e.g. Apricot Phase 3 introduced dynamic (EIP-1559-style) fees; Durango brought the
        // Cancun-era opcodes (EIP-1153/5656/6780) to the C-Chain.
    }

    public void ApplyToChainSpec(ChainSpec chainSpec) { }

    public void AddTransitions(SortedSet<ulong> blockNumbers, SortedSet<ulong> timestamps)
    {
        AddIfNotNull(timestamps, ApricotPhase3Timestamp);
        AddIfNotNull(timestamps, DurangoTimestamp);
        AddIfNotNull(timestamps, EtnaTimestamp);
        AddIfNotNull(timestamps, FortunaTimestamp);
        AddIfNotNull(timestamps, GraniteTimestamp);
    }

    private static void AddIfNotNull(SortedSet<ulong> timestamps, ulong? timestamp)
    {
        if (timestamp is not null)
        {
            timestamps.Add(timestamp.Value);
        }
    }
}
