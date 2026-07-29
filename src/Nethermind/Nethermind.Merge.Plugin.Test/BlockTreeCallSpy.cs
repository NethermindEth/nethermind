// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>
/// Counts <see cref="IBlockFinder.FindHeader"/> calls on a real block tree.
/// </summary>
internal sealed class BlockTreeCallSpy(IBlockTree inner) : BlockTreeTestDouble(inner)
{
    public int FindHeaderCalls { get; private set; }

    public void ResetCounters() => FindHeaderCalls = 0;

    public static (IBlockTree Proxy, BlockTreeCallSpy Spy) Wrap(IBlockTree inner)
    {
        BlockTreeCallSpy spy = new(inner);
        return (spy, spy);
    }

    public override BlockHeader? FindHeader(Hash256 blockHash, BlockTreeLookupOptions options, ulong? blockNumber = null)
    {
        FindHeaderCalls++;
        return base.FindHeader(blockHash, options, blockNumber);
    }

    public override BlockHeader? FindHeader(ulong blockNumber, BlockTreeLookupOptions options)
    {
        FindHeaderCalls++;
        return base.FindHeader(blockNumber, options);
    }
}
