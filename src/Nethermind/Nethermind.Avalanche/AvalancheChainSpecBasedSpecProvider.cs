// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Avalanche;

/// <summary>
/// Produces per-fork <see cref="AvalancheReleaseSpec"/> instances mapping the Avalanche C-Chain
/// (Coreth) network upgrades onto EVM EIP flags. Mainnet gating: pre-Istanbul + Istanbul at block 0,
/// Berlin at the Apricot-Phase-2 block, London (EIP-1559) at the Apricot-Phase-3 block, then
/// Durango (Shanghai), Etna (Cancun subset), Fortuna (ACP-176) and Granite (P256VERIFY) by timestamp.
/// </summary>
/// <remarks>
/// Encodes the C-Chain's divergences from Ethereum: no blob transactions (EIP-4844), no beacon
/// withdrawals (EIP-4895) despite Shanghai, and no beacon-root system call (EIP-4788). The fee-market
/// and state-root (5-field account RLP, storage-key transform) parity concerns live outside the spec
/// in the block processor / state layers.
/// </remarks>
public class AvalancheChainSpecBasedSpecProvider(
    ChainSpec chainSpec,
    AvalancheChainSpecEngineParameters engineParameters,
    ILogManager logManager)
    : ChainSpecBasedSpecProvider(chainSpec, logManager)
{
    private static readonly UInt256 OneGwei = 1_000_000_000;

    protected override ReleaseSpec CreateEmptyReleaseSpec() => new AvalancheReleaseSpec();

    protected override ReleaseSpec CreateReleaseSpec(ChainSpec chainSpec, ulong releaseStartBlock, ulong? releaseStartTimestamp = null)
    {
        AvalancheReleaseSpec spec = (AvalancheReleaseSpec)base.CreateReleaseSpec(chainSpec, releaseStartBlock, releaseStartTimestamp);
        ulong timestamp = releaseStartTimestamp ?? 0UL;

        // Launch (block 0): Frontier..Istanbul.
        spec.IsEip2Enabled = true;
        spec.IsEip7Enabled = true;
        spec.IsEip100Enabled = true;
        spec.IsEip140Enabled = true;
        spec.IsEip150Enabled = true;
        spec.IsEip155Enabled = true;
        spec.IsEip158Enabled = true;
        spec.IsEip160Enabled = true;
        spec.IsEip170Enabled = true;
        spec.IsEip196Enabled = true;
        spec.IsEip197Enabled = true;
        spec.IsEip198Enabled = true;
        spec.IsEip211Enabled = true;
        spec.IsEip214Enabled = true;
        spec.IsEip649Enabled = true;
        spec.IsEip658Enabled = true;
        spec.IsEip145Enabled = true;
        spec.IsEip1014Enabled = true;
        spec.IsEip1052Enabled = true;
        spec.IsEip1283Enabled = false; // Petersburg removed EIP-1283; Istanbul re-adds as EIP-2200
        spec.IsEip1234Enabled = true;
        spec.IsEip152Enabled = true;
        spec.IsEip1108Enabled = true;
        spec.IsEip1344Enabled = true;
        spec.IsEip1884Enabled = true;
        spec.IsEip2028Enabled = true;
        spec.IsEip2200Enabled = true;

        bool apricotPhase2 = engineParameters.ApricotPhase2BlockNumber is { } ap2Block && releaseStartBlock >= (ulong)ap2Block;
        bool apricotPhase3 = engineParameters.ApricotPhase3BlockNumber is { } ap3Block && releaseStartBlock >= (ulong)ap3Block;
        bool durango = engineParameters.DurangoTimestamp is { } durangoTs && timestamp >= durangoTs;
        bool etna = engineParameters.EtnaTimestamp is { } etnaTs && timestamp >= etnaTs;
        bool fortuna = engineParameters.FortunaTimestamp is { } fortunaTs && timestamp >= fortunaTs;
        bool granite = engineParameters.GraniteTimestamp is { } graniteTs && timestamp >= graniteTs;

        // Apricot Phase 2 == Berlin.
        if (apricotPhase2)
        {
            spec.IsEip2565Enabled = true;
            spec.IsEip2929Enabled = true;
            spec.IsEip2930Enabled = true;
        }

        // Apricot Phase 3 == London (EIP-1559 dynamic base fee).
        if (apricotPhase3)
        {
            spec.IsEip1559Enabled = true;
            spec.IsEip3198Enabled = true;
            spec.IsEip3529Enabled = true;
            spec.IsEip3541Enabled = true;
            spec.Eip1559TransitionBlock = (ulong)engineParameters.ApricotPhase3BlockNumber!.Value;
        }

        // Durango == Shanghai (no beacon withdrawals).
        if (durango)
        {
            spec.IsEip3651Enabled = true;
            spec.IsEip3855Enabled = true;
            spec.IsEip3860Enabled = true;
        }

        // Etna == Cancun EVM subset (no blobs); min base fee drops to 1 gwei (ACP-125).
        if (etna)
        {
            spec.IsEip1153Enabled = true;
            spec.IsEip5656Enabled = true;
            spec.IsEip6780Enabled = true;
            spec.Eip1559BaseFeeMinValue = OneGwei;
        }

        // Granite == P256VERIFY precompile (RIP-7212 / ACP-204).
        if (granite)
        {
            spec.IsRip7212Enabled = true;
        }

        // Avalanche divergences from Ethereum.
        spec.IsEip4844Enabled = false; // no blob transactions
        spec.IsEip4895Enabled = false; // no beacon withdrawals despite Shanghai
        spec.IsEip4788Enabled = false; // no beacon-root system call (header field forced zero)
        spec.IsEip7702Enabled = false;

        spec.IsApricotPhase2Enabled = apricotPhase2;
        spec.IsApricotPhase3Enabled = apricotPhase3;
        spec.IsDurangoEnabled = durango;
        spec.IsEtnaEnabled = etna;
        spec.IsFortunaEnabled = fortuna;
        spec.IsGraniteEnabled = granite;

        return spec;
    }
}
