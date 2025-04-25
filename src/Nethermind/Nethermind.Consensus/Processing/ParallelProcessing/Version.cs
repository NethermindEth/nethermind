// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Processing.ParallelProcessing;

/// <summary>
/// Represents a version of transaction execution.
/// </summary>
/// <param name="TxIndex">Tx index in block - unique tx id</param>
/// <param name="Incarnation">Number indicating which time this transaction is executing.</param>
public readonly record struct Version(ushort TxIndex, ushort Incarnation)
{
    public bool IsEmpty => TxIndex == ushort.MaxValue && Incarnation == ushort.MaxValue;

    /// <summary>
    /// Empty version is used as a special value when no execution should happen
    /// </summary>
    public static Version Empty { get; } = new(ushort.MaxValue, ushort.MaxValue);
    public override string ToString() => $"Tx {TxIndex}, Incarnation {Incarnation}";

    public void Deconstruct(out ushort txIndex, out ushort incarnation)
    {
        txIndex = TxIndex;
        incarnation = Incarnation;
    }
}
