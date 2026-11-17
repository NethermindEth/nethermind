// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test.Handlers;

[Parallelizable(ParallelScope.All)]
public class GetPayloadBodiesPrefetchTests
{
    [Test]
    public void Prefetches_requested_bodies_a_window_at_a_time([Values(1, 32, 40)] int count, [Values] bool byHash, [Values] bool v2)
    {
        using PrefetchingTestDb blockDb = new();
        BlockStore blockStore = new(blockDb);
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        BlockHeader[] headers = new BlockHeader[count];
        for (int i = 0; i < count; i++)
        {
            headers[i] = Build.A.BlockHeader.WithNumber((ulong)i + 1).TestObject;
            blockTree.FindHeader(headers[i].Number, Arg.Any<BlockTreeLookupOptions>()).Returns(headers[i]);
            blockTree.FindHeader(headers[i].Hash!, Arg.Any<BlockTreeLookupOptions>()).Returns(headers[i]);
        }
        blockTree.Head.Returns(Build.A.Block.WithNumber((ulong)count).TestObject);
        Hash256[] hashes = headers.Select(static header => header.Hash!).ToArray();
        IBlockAccessListStore balStore = Substitute.For<IBlockAccessListStore>();

        Action request = (byHash, v2) switch
        {
            (true, false) => () => new GetPayloadBodiesByHashV1Handler(blockTree, blockStore, LimboLogs.Instance).Handle(hashes),
            (true, true) => () => new GetPayloadBodiesByHashV2Handler(blockTree, LimboLogs.Instance, balStore, blockStore).Handle(hashes),
            (false, false) => () => new GetPayloadBodiesByRangeV1Handler(blockTree, blockStore, LimboLogs.Instance).Handle(1, (ulong)count).Wait(),
            (false, true) => () => new GetPayloadBodiesByRangeV2Handler(blockTree, LimboLogs.Instance, balStore, blockStore).Handle(1, (ulong)count).Wait(),
        };
        request();

        byte[][][] windows = headers
            .Select(static header => Bytes.Concat(header.Number.ToBigEndianByteArray(), header.Hash!.BytesToArray()))
            .Chunk(32)
            .Where(static window => window.Length > 1)
            .ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockDb.Prefetches, Is.EqualTo(windows));
            blockTree.Received(!byHash && count > 1 ? 1 : 0).PrefetchCanonicalHeaders(1, count);
            blockTree.Received(byHash && count > 1 ? 1 : 0).PrefetchHeaders(hashes);
        }
    }
}
