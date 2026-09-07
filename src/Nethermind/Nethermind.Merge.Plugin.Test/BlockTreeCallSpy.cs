// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>
/// Records every height-hinted header lookup made against a real block tree, the shape
/// <see cref="IBlockFinderExtensions.FindParentHeader"/> issues when it passes the parent's height.
/// </summary>
/// <remarks>
/// The spy decorates the container's singleton block tree, which background components share and probe on
/// their own threads - under the flat layout the persistence pipeline resolves the finalized state root that
/// way. Recording is therefore thread-safe, and assertions have to key on the specific lookup under test
/// rather than on a total call count that any of those threads can bump at any moment.
/// </remarks>
internal sealed class BlockTreeCallSpy(IBlockTree inner) : BlockTreeTestDouble(inner)
{
    private readonly ConcurrentDictionary<(ValueHash256 Hash, ulong Number), byte> _parentProbes = new();

    public void Reset() => _parentProbes.Clear();

    public bool WasProbedAsParent(Hash256 blockHash, ulong blockNumber) =>
        _parentProbes.ContainsKey((blockHash.ValueHash256, blockNumber));

    public static (IBlockTree Proxy, BlockTreeCallSpy Spy) Wrap(IBlockTree inner)
    {
        BlockTreeCallSpy spy = new(inner);
        return (spy, spy);
    }

    public override BlockHeader? FindHeader(Hash256 blockHash, BlockTreeLookupOptions options, ulong? blockNumber = null)
    {
        if (blockNumber is not null)
        {
            _parentProbes.TryAdd((blockHash.ValueHash256, blockNumber.Value), 0);
        }

        return base.FindHeader(blockHash, options, blockNumber);
    }
}
