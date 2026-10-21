// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// // SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Taiko.Precompiles;

namespace Nethermind.Taiko.TaikoSpec;

public interface ITaikoReleaseSpec : IReleaseSpec
{
    public bool IsOntakeEnabled { get; }
    public bool IsPacayaEnabled { get; }
    public bool IsShastaEnabled { get; }
    public bool IsUnzenEnabled { get; }
    public ulong UnzenBlockZkGasLimit { get; }
    public ulong UnzenTxIntrinsicZkGas { get; }

    /// <summary>
    /// Resolved 256-entry per-opcode ZK gas multiplier table for Unzen, indexed by opcode byte.
    /// Sourced from the chainspec's active <c>unzenZkGasSchedules</c> entry.
    /// </summary>
    ReadOnlyMemory<ushort> UnzenOpcodeZkGasMultipliers { get; }

    /// <summary>
    /// Resolved per-precompile ZK gas multiplier dictionary for Unzen, keyed by full precompile
    /// address. Sourced from the chainspec's active <c>unzenZkGasSchedules</c> entry. Addresses
    /// not in the dictionary are charged at <see cref="ZkGas.ZkGasSchedule.FailsafeMultiplier"/>.
    /// </summary>
    FrozenDictionary<AddressAsKey, ushort> UnzenPrecompileZkGasMultipliers { get; }

    public bool UseSurgeGasPriceOracle { get; }
    public Address TaikoL2Address { get; }
    public bool IsRip7728Enabled { get; }
    public bool IsL1StaticCallEnabled { get; }

    internal static FrozenSet<AddressAsKey> BuildTaikoPrecompilesCache(
        FrozenSet<AddressAsKey> baseCache, bool isRip7728Enabled, bool isL1StaticCallEnabled)
    {
        HashSet<AddressAsKey> cache = [.. baseCache];
        if (isRip7728Enabled) cache.Add(L1SloadPrecompile.Address);
        if (isL1StaticCallEnabled) cache.Add(L1StaticCallPrecompile.Address);
        return cache.ToFrozenSet();
    }
}
