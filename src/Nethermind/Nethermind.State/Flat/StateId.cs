// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat;

public readonly record struct StateId(long blockNumber, ValueHash256 stateRoot) : IComparable<StateId>
{
    public int CompareTo(StateId other)
    {
        var blockNumberComparison = blockNumber.CompareTo(other.blockNumber);
        if (blockNumberComparison != 0) return blockNumberComparison;
        return stateRoot.CompareTo(other.stateRoot);
    }
}
