// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;

/// <summary>
/// Identifies a specific execution attempt of a transaction within a block under block-STM.
/// </summary>
/// <param name="TxIndex">Tx index in block — unique tx id.</param>
/// <param name="Incarnation">Which execution attempt this is (re-execution increments).</param>
/// <remarks>
/// Renamed from <c>Version</c> to avoid the collision with <see cref="System.Version"/>.
/// </remarks>
public readonly record struct TxVersion(int TxIndex, int Incarnation)
{
    public bool IsEmpty => TxIndex == -1;

    /// <summary>Sentinel signalling "no execution" — used as the "read from DB" version.</summary>
    public static TxVersion Empty { get; } = new(-1, -1);

    public override string ToString() => IsEmpty ? "Empty" : $"Tx {TxIndex}, Incarnation {Incarnation}";
}
