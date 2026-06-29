// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Avalanche;

/// <summary>
/// Chainspec <c>engine.avalanche</c> parameters: the Avalanche C-Chain (Coreth) network-upgrade
/// activation points that drive EVM hard-fork selection. Mainnet values are source-verified against
/// <c>ava-labs/coreth</c> and <c>ava-labs/avalanchego/upgrade</c>.
/// </summary>
/// <remarks>
/// On mainnet the EVM Berlin/London transitions gate by <b>block number</b> (Apricot Phase 2/3),
/// while every upgrade from Durango onward gates by <b>block timestamp</b>. The mapping of each
/// upgrade to EVM EIP flags is applied in <see cref="AvalancheChainSpecBasedSpecProvider"/>.
/// </remarks>
public class AvalancheChainSpecEngineParameters : IChainSpecEngineParameters
{
    public string? EngineName => SealEngineType;
    public string? SealEngineType => Core.SealEngineType.Avalanche;

    // Berlin / London gate by block number on mainnet (AP2 = 1640340, AP3 = 3308552).
    public long? ApricotPhase2BlockNumber { get; set; }
    public long? ApricotPhase3BlockNumber { get; set; }

    // Timestamp-gated upgrades (Unix seconds).
    public ulong? ApricotPhase1Timestamp { get; set; }
    public ulong? ApricotPhase2Timestamp { get; set; }
    public ulong? ApricotPhase3Timestamp { get; set; }
    public ulong? ApricotPhase4Timestamp { get; set; }
    public ulong? ApricotPhase5Timestamp { get; set; }
    public ulong? ApricotPhasePre6Timestamp { get; set; }
    public ulong? ApricotPhase6Timestamp { get; set; }
    public ulong? ApricotPhasePost6Timestamp { get; set; }
    public ulong? BanffTimestamp { get; set; }
    public ulong? CortinaTimestamp { get; set; }
    public ulong? DurangoTimestamp { get; set; }   // Shanghai EVM (PUSH0/EIP-3860/EIP-3651); no withdrawals
    public ulong? EtnaTimestamp { get; set; }       // Cancun EVM (EIP-1153/5656/6780); no blob txs; min base fee 1 gwei
    public ulong? FortunaTimestamp { get; set; }    // ACP-176 dynamic gas target/price
    public ulong? GraniteTimestamp { get; set; }    // P256VERIFY precompile (RIP-7212)

    // C-Chain block gas limit was raised to 15,000,000 at Cortina.
    public long? CortinaGasLimit { get; set; }

    public void ApplyToReleaseSpec(ReleaseSpec spec, ulong startBlock, ulong? startTimestamp) { }

    public void ApplyToChainSpec(ChainSpec chainSpec) { }

    public void AddTransitions(SortedSet<ulong> blockNumbers, SortedSet<ulong> timestamps)
    {
        if (ApricotPhase2BlockNumber is not null) blockNumbers.Add((ulong)ApricotPhase2BlockNumber.Value);
        if (ApricotPhase3BlockNumber is not null) blockNumbers.Add((ulong)ApricotPhase3BlockNumber.Value);
        AddIfNotNull(timestamps, CortinaTimestamp);
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
