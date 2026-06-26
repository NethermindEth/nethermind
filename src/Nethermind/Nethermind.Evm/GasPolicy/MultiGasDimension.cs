// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.GasPolicy;

/// <summary>
/// Resource dimension tracked by <see cref="MultiDimensionalGasPolicy"/>.
/// </summary>
/// <remarks>
/// Mirrors the partitioning used by go-ethereum's multi-gas accounting (the OffchainLabs
/// <c>ResourceKind</c> model): each operation's existing gas cost is attributed to exactly one
/// resource, so the per-dimension amounts sum back to the legacy single-dimensional cost. The
/// breakdown is therefore pure instrumentation — the spendable gas and consensus outcome are
/// unchanged. The ordinals index <see cref="MultiDimensionalGasPolicy"/>'s usage vector and are
/// not consensus-observable.
/// </remarks>
public enum MultiGasDimension : byte
{
    /// <summary>Raw EVM/opcode execution, memory expansion, hashing, value transfer.</summary>
    Computation = 0,

    /// <summary>Reads of existing state (cold SLOAD/account access, the cold-minus-warm delta).</summary>
    StorageAccessRead = 1,

    /// <summary>Writes to existing state (SSTORE reset, SELFDESTRUCT teardown).</summary>
    StorageAccessWrite = 2,

    /// <summary>Persistent state growth (new storage slot, new account, contract/code deposit).</summary>
    StorageGrowth = 3,

    /// <summary>Append-only history growth (LOG0–LOG4).</summary>
    HistoryGrowth = 4,
}
