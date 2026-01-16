// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.State.Flat;

public readonly record struct StateId(long BlockNumber, ValueHash256 StateRoot) : IComparable<StateId>
{
    public StateId(BlockHeader? header) : this(header?.Number ?? -1, header?.StateRoot ?? Keccak.EmptyTreeHash)
    {
    }

    public static StateId PreGenesis = new StateId(-1, Keccak.EmptyTreeHash);

    public int CompareTo(StateId other)
    {
        int blockNumberComparison = BlockNumber.CompareTo(other.BlockNumber);
        if (blockNumberComparison != 0) return blockNumberComparison;
        return StateRoot.CompareTo(other.StateRoot);
    }
}
