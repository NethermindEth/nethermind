// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat;

public readonly record struct StateId(ulong BlockNumber, in ValueHash256 StateRoot) : IComparable<StateId>
{
    public StateId(BlockHeader? header) : this(header is null ? ulong.MaxValue : header.Number, header?.StateRoot ?? Keccak.EmptyTreeHash)
    {
    }

    // Reserved sentinels at the top of the block-number range; these heights are never reached by a
    // real chain, so they cannot collide with a real state id (e.g. block 0 with the empty trie root).
    public static readonly StateId PreGenesis = new(ulong.MaxValue, Keccak.EmptyTreeHash);
    public static readonly StateId Sync = new(ulong.MaxValue - 1, Keccak.EmptyTreeHash);

    public int CompareTo(StateId other)
    {
        int blockNumberComparison = BlockNumber.CompareTo(other.BlockNumber);
        if (blockNumberComparison != 0) return blockNumberComparison;
        return StateRoot.CompareTo(other.StateRoot);
    }
}
