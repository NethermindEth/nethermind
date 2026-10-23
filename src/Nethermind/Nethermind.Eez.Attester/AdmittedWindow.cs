// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Stateless;

namespace Nethermind.Eez.Attester;

internal sealed record AdmittedWindow(ulong FromBlock, ulong ToBlock, byte[] PostBatchCalldata, EezStatelessBlock[] Blocks, (Hash256 Hash, Hash256 ParentHash)[] Claims)
    : IDisposable
{
    internal Prove.ProveHeader? NativeHeader { get; init; }
    internal PreparedOrdinaryBlock?[] Prepared { get; init; } = [];
    internal IReadOnlyList<PreparedCache.BorrowedRange> CacheBorrows { get; init; } = [];

    public void Dispose()
    {
        foreach (PreparedCache.BorrowedRange range in CacheBorrows) range.Dispose();
        foreach (EezStatelessBlock block in Blocks)
        {
            block.Witness.Dispose();
        }
    }
}
