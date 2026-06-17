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
/// Unzen ZK gas defaults and resolvers: the default scalars (block limit, intrinsic, fail-safe)
/// plus the resolvers (<see cref="BuildOpcodeTable"/>, <see cref="BuildPrecompileTable"/>) that
/// turn the sparse per-schedule maps from the chainspec into runtime multiplier tables.
/// </summary>
public static class ZkGasSchedule
{
    /// <summary>Default Unzen block ZK gas limit. Chainspecs may override via <c>unzenBlockZkGasLimit</c>.</summary>
    public const ulong BlockZkGasLimit = 100_000_000;

    /// <summary>
    /// Default flat ZK gas charged per transaction before any opcode runs; covers proving cost of
    /// sender ecrecovery. Chainspecs may override via <c>unzenTxIntrinsicZkGas</c>.
    /// </summary>
    public const ulong TxIntrinsicZkGas = 243_000;

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

    /// <summary>Fixed raw-gas estimate for CALL when it opens a child frame.</summary>
    public const ulong SpawnEstimateCall = 12_500;
    /// <summary>Fixed raw-gas estimate for CALLCODE when it opens a child frame.</summary>
    public const ulong SpawnEstimateCallCode = 12_500;
    /// <summary>Fixed raw-gas estimate for DELEGATECALL when it opens a child frame.</summary>
    public const ulong SpawnEstimateDelegateCall = 3_500;
    /// <summary>Fixed raw-gas estimate for STATICCALL when it opens a child frame.</summary>
    public const ulong SpawnEstimateStaticCall = 3_500;
    /// <summary>Fixed raw-gas estimate for CREATE when it opens a child frame.</summary>
    public const ulong SpawnEstimateCreate = 37_000;
    /// <summary>Fixed raw-gas estimate for CREATE2 when it opens a child frame.</summary>
    public const ulong SpawnEstimateCreate2 = 44_500;
}
