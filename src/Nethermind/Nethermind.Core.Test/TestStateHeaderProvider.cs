// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Test;

/// <summary>Header provider stub that answers every parent lookup with <see cref="Parent"/>.</summary>
public sealed class TestStateHeaderProvider : IStateHeaderProvider
{
    public BlockHeader? Parent { get; set; }
    public int LookupCalls { get; private set; }
    public bool ThrowOnLookup { get; set; }

    public BlockHeader? FindParentHeader(BlockHeader target)
    {
        LookupCalls++;
        if (ThrowOnLookup) throw new InvalidOperationException("Parent lookup must not be called for genesis.");
        return Parent;
    }

    public ulong FinalizedBlockNumber => 0;

    public BlockHeader? GetFinalizedHeader(ulong blockNumber) => null;
}
