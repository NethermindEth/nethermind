// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Processing.ParallelProcessing;

public readonly record struct Version(ushort TxIndex, ushort Incarnation)
{
    public bool IsEmpty => TxIndex == ushort.MaxValue && Incarnation == ushort.MaxValue;
    public static Version Empty { get; } = new(ushort.MaxValue, ushort.MaxValue);
    public override string ToString() => $"Tx {TxIndex}, Incarnation {Incarnation}";
}
