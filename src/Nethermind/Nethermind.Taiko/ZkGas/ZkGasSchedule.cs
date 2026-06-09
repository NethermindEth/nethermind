// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Exceptions;

namespace Nethermind.Taiko.ZkGas;

/// <summary>
/// Consensus-owned ZK gas schedule constants for the Unzen hardfork, plus the resolver that
/// builds a runtime 256-entry multiplier table from a sparse chainspec override map.
/// </summary>
/// <remarks>
/// The recalibrated default tables themselves no longer live in code: every chainspec that
/// activates Unzen lists its own ordered set of <c>unzenZkGasSchedules</c>, and the spec provider
/// picks the active schedule by block timestamp. The taiko-alethia chainspec carries the
/// recalibrated tables from taiko-mono#21720 / alethia-reth#187; a network that finalized Unzen
/// under an earlier schedule can pin its own table the same way.
/// </remarks>
public static class ZkGasSchedule
{
    /// <summary>Chain id of the Taiko Alethia mainnet.</summary>
    public const ulong TaikoMainnetChainId = 167_000;

    /// <summary>Chain id of the Taiko devnet.</summary>
    public const ulong TaikoDevnetChainId = 167_001;

    /// <summary>Chain id of the Taiko Masaya testnet.</summary>
    public const ulong TaikoMasayaChainId = 167_011;

    /// <summary>Chain id of the Taiko Hoodi testnet.</summary>
    public const ulong TaikoHoodiChainId = 167_013;

    /// <summary>Default Unzen block ZK gas limit.</summary>
    public const ulong BlockZkGasLimit = 100_000_000;

    /// <summary>Fixed ZK gas charged per transaction before any opcode runs; covers proving cost of sender ecrecovery.</summary>
    public const ulong TxIntrinsicZkGas = 243_000;

    /// <summary>
    /// Mainnet batch-lookup threshold: the first allowed block id (first Shasta block).
    /// Resolved batch lookup results <em>strictly less than</em> this value are reported
    /// to the driver as JSON null; the value itself passes through unchanged. Sourced
    /// from taiko-geth PR #558 and alethia-reth PR #177. Named for the comparison
    /// semantics rather than the upstream "last Pacaya" label, which is inverted
    /// relative to the strict-<c>&lt;</c> operator.
    /// </summary>
    public const ulong TaikoMainnetBatchLookupThreshold = 4_990_434;

    /// <summary>
    /// Hoodi batch-lookup threshold: the first allowed block id (first Shasta block).
    /// Resolved batch lookup results <em>strictly less than</em> this value are reported
    /// to the driver as JSON null; the value itself passes through unchanged. Sourced
    /// from taiko-geth PR #558 and alethia-reth PR #177.
    /// </summary>
    public const ulong TaikoHoodiBatchLookupThreshold = 3_951_005;

    /// <summary>
    /// Returns the per-network minimum block id for batch lookup RPC results, or
    /// <c>null</c> on networks with no threshold (Devnet, Masaya, unknown). When a
    /// threshold is configured, <c>taikoAuth_last{Certain,}{L1Origin,BlockID}ByBatchID</c>
    /// must report JSON null for any resolved block id strictly below it. Mirrors
    /// <c>batchLookupBlockThresholds</c> in taiko-geth (PR #558) and
    /// <c>batch_lookup_last_pacaya_block_threshold</c> in alethia-reth (PR #177).
    /// </summary>
    /// <param name="chainId">Chain id from <see cref="Nethermind.Core.Specs.ISpecProvider.ChainId"/>.</param>
    public static ulong? ResolveBatchLookupThreshold(ulong chainId) => chainId switch
    {
        TaikoMainnetChainId => TaikoMainnetBatchLookupThreshold,
        TaikoHoodiChainId => TaikoHoodiBatchLookupThreshold,
        _ => null,
    };

    /// <summary>Fail-safe multiplier for any opcode or precompile not explicitly listed.</summary>
    public const ushort FailsafeMultiplier = ushort.MaxValue;

    /// <summary>
    /// Resolves a 256-entry opcode multiplier table from a sparse chainspec map. Listed indices
    /// take the supplied multiplier; every other index fills with <see cref="FailsafeMultiplier"/>.
    /// A null or empty <paramref name="entries"/> resolves to a 256-entry fail-safe table — useful
    /// pre-Unzen, where the meter is constructed but its charges go unused.
    /// </summary>
    /// <param name="entries">Index (0–255) → multiplier (0–<see cref="ushort.MaxValue"/>) map from the chainspec, or null.</param>
    /// <exception cref="InvalidConfigurationException">An index or multiplier is out of range.</exception>
    public static ReadOnlyMemory<ushort> BuildOpcodeTable(IReadOnlyDictionary<long, long>? entries)
    {
        ushort[] table = new ushort[256];
        table.AsSpan().Fill(FailsafeMultiplier);

        if (entries is null || entries.Count == 0)
        {
            return table;
        }

        foreach ((long index, long multiplier) in entries)
        {
            if (index is < 0 or > byte.MaxValue)
            {
                throw new InvalidConfigurationException(
                    $"Unzen opcode ZK gas multiplier index {index} is outside the valid range [0, {byte.MaxValue}].",
                    ExitCodes.ForbiddenOptionValue);
            }

            ValidateMultiplier(multiplier, $"opcode 0x{index:x2}");
            table[index] = (ushort)multiplier;
        }

        return table;
    }

    /// <summary>
    /// Resolves a precompile multiplier dictionary from a sparse chainspec map keyed by full
    /// 20-byte hex address. Both canonical EVM precompiles (e.g. <c>0x…0001</c>) and Taiko
    /// precompiles in the higher range (e.g. <c>0x…010001</c> L1Sload) live in the same table
    /// without colliding. Addresses not listed are charged at <see cref="FailsafeMultiplier"/> by
    /// the meter (which performs a TryGet lookup, treating absence as fail-safe).
    /// </summary>
    /// <param name="entries">Hex address → multiplier (0–<see cref="ushort.MaxValue"/>) map from the chainspec, or null.</param>
    /// <exception cref="InvalidConfigurationException">An address is malformed or a multiplier is out of range.</exception>
    public static FrozenDictionary<AddressAsKey, ushort> BuildPrecompileTable(IReadOnlyDictionary<string, long>? entries)
    {
        if (entries is null || entries.Count == 0)
        {
            return FrozenDictionary<AddressAsKey, ushort>.Empty;
        }

        Dictionary<AddressAsKey, ushort> table = new(entries.Count);
        foreach ((string addressHex, long multiplier) in entries)
        {
            Address address;
            try
            {
                address = new Address(addressHex);
            }
            catch (Exception ex)
            {
                throw new InvalidConfigurationException(
                    $"Unzen precompile ZK gas multiplier address '{addressHex}' is not a valid 20-byte hex address: {ex.Message}",
                    ExitCodes.ForbiddenOptionValue);
            }

            ValidateMultiplier(multiplier, $"precompile {addressHex}");
            table[address] = (ushort)multiplier;
        }

        return table.ToFrozenDictionary();
    }

    private static void ValidateMultiplier(long multiplier, string label)
    {
        if (multiplier is < 0 or > ushort.MaxValue)
        {
            throw new InvalidConfigurationException(
                $"Unzen ZK gas multiplier {multiplier} for {label} is outside the valid range [0, {ushort.MaxValue}].",
                ExitCodes.ForbiddenOptionValue);
        }
    }

    // Fixed raw-gas estimates for spawn opcodes (used when the opcode actually opens a child frame).
    public const ulong SpawnEstimateCall = 12_500;
    public const ulong SpawnEstimateCallCode = 12_500;
    public const ulong SpawnEstimateDelegateCall = 3_500;
    public const ulong SpawnEstimateStaticCall = 3_500;
    public const ulong SpawnEstimateCreate = 37_000;
    public const ulong SpawnEstimateCreate2 = 44_500;
}
