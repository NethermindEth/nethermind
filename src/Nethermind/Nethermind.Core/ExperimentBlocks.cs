// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.Core;

/// <summary>The blocks the hidden experiment flags apply to: all of them, or only those with even or odd numbers.</summary>
/// <remarks>Alternating blocks let one node start measure a flag both on and off over the same chain.</remarks>
public static class ExperimentBlocks
{
    private static ulong _current = ulong.MaxValue;

    /// <summary>0 applies the flags to every block, 1 to even block numbers only, 2 to odd ones only; set once at startup.</summary>
    public static int Parity { get; set; }

    public static bool Apply(ulong blockNumber) => Parity switch
    {
        1 => (blockNumber & 1) == 0,
        2 => (blockNumber & 1) == 1,
        _ => true
    };

    /// <summary>Records the block being warmed, for flags read where its number is not at hand.</summary>
    public static void Enter(ulong blockNumber) => Volatile.Write(ref _current, blockNumber);

    /// <summary>Whether the flags apply to the block being warmed.</summary>
    public static bool ApplyToCurrent => Parity == 0 || Apply(Volatile.Read(ref _current));
}
