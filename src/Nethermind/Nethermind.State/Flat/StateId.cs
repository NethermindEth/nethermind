// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.State.Flat;

public readonly record struct StateId(long blockNumber, ValueHash256 stateRoot) : IComparable<StateId>
{
    public StateId(BlockHeader? header) : this(header?.Number ?? -1, header?.StateRoot ?? Keccak.EmptyTreeHash)
    {
    }

    public int CompareTo(StateId other)
    {
        var blockNumberComparison = blockNumber.CompareTo(other.blockNumber);
        if (blockNumberComparison != 0) return blockNumberComparison;
        return stateRoot.CompareTo(other.stateRoot);
    }
}
