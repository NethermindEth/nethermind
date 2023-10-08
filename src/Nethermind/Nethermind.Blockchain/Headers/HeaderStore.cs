// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Headers;

public class HeaderStore: IHeaderStore
{
    // SyncProgressResolver MaxLookupBack is 128, add 16 wiggle room
    private const int CacheSize = 128 + 16;

    private readonly IDb _db;
    private readonly HeaderDecoder _headerDecoder = new();
    private readonly LruCache<ValueKeccak, BlockHeader> _headerCache =
        new(CacheSize, CacheSize, "headers");


    public HeaderStore(IDb db)
    {
        _db = db;
    }

    public void Store(BlockHeader header)
    {
        // validate hash here
        // using previously received header RLPs would allows us to save 2GB allocations on a sample
        // 3M Goerli blocks fast sync
        using NettyRlpStream newRlp = _headerDecoder.EncodeToNewNettyStream(header);
        _db.Set(header.Hash, newRlp.AsSpan());
    }

    public BlockHeader Get(Keccak blockHash, bool shouldCache)
    {
        return _db.Get(blockHash, _headerDecoder, _headerCache, shouldCache: false);
    }

    public void Cache(BlockHeader header)
    {
        _headerCache.Set(header.Hash, header);
    }

    public void Delete(Keccak blockHash)
    {
        _db.Delete(blockHash);
        _headerCache.Delete(blockHash);
    }
}
