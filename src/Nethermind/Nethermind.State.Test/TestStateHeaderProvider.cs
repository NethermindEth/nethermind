// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using Nethermind.Core;

namespace Nethermind.Store.Test;

internal sealed class TestStateHeaderProvider : IStateHeaderProvider
{
    public static TestStateHeaderProvider Unavailable { get; } = new();

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
