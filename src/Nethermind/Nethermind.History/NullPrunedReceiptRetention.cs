// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.History;

public sealed class NullPrunedReceiptRetention : IPrunedReceiptRetention
{
    public static readonly NullPrunedReceiptRetention Instance = new();

    private static readonly FrozenSet<ulong> None = FrozenSet<ulong>.Empty;

    private NullPrunedReceiptRetention()
    {
    }

    public bool ShouldRetainReceipts(Block block) => false;

    /// <summary>Answers for any span, because the answer is always nothing - which is what lets a node with no
    /// retention configured reclaim by range end to end.</summary>
    public IReadOnlySet<ulong> RetainedHeights(ulong fromInclusive, ulong toExclusive, out ulong answeredFrom, out ulong answeredTo)
    {
        answeredFrom = fromInclusive;
        answeredTo = toExclusive;
        return None;
    }
}
