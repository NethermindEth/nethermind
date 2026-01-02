// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using NonBlocking;

namespace Nethermind.Core.Test;

public class CachedBlockTreeBuilder
{
    private static readonly ConcurrentDictionary<string, CachedDb> _cachedDbs = new();
    private record CachedDb(
        MemDb blocksDb,
        MemDb metadataDb,
        MemDb headersDb,
        MemDb blockNumbersDb,
        MemDb blockInfo
    );

    public static IBlockTree BuildCached(string key, Func<BlockTreeBuilder> blockTreeBuilderFactory)
    {
        if (_cachedDbs.TryGetValue(key, out CachedDb? db))
        {
            return Build.A.BlockTree()
                .WithBlocksDb(MemDb.CopyFrom(db.blocksDb))
                .WithMetadataDb(MemDb.CopyFrom(db.metadataDb))
                .WithHeadersDb(MemDb.CopyFrom(db.headersDb))
                .WithBlocksNumberDb(MemDb.CopyFrom(db.blockNumbersDb))
                .WithBlockInfoDb(MemDb.CopyFrom(db.blockInfo))
                .TestObject;
        }
        else
        {
            BlockTreeBuilder builder = blockTreeBuilderFactory();
            CachedDb cachedValue = new CachedDb(
                MemDb.CopyFrom(builder.BlocksDb),
                MemDb.CopyFrom(builder.MetadataDb),
                MemDb.CopyFrom(builder.HeadersDb),
                MemDb.CopyFrom(builder.BlockNumbersDb),
                MemDb.CopyFrom(builder.BlockInfoDb)
            );
            _cachedDbs.GetOrAdd(key, cachedValue);

            return builder.TestObject;
        }
    }

    public static IBlockTree OfLength(int length)
    {
        return BuildCached($"{nameof(CachedBlockTreeBuilder)}-{length}", () => Build.A.BlockTree().OfChainLength(length));
    }
}
