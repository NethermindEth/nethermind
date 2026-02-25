// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.Blockchain.Headers;

public class BlockAccessListStore(
    [KeyFilter(DbNames.BlockAccessLists)] IDb balDb,
    BlockAccessListDecoder? decoder = null)
    : IBlockAccessListStore
{
    // SyncProgressResolver MaxLookupBack is 256, add 16 wiggle room
    // public const int CacheSize = 256 + 16;

    private readonly BlockAccessListDecoder _balDecoder = decoder ?? new();
    // private readonly ClockCache<ValueHash256, BlockHeader> _headerCache = new(CacheSize);

    public void Insert(Hash256 blockHash, BlockAccessList bal)
        => balDb.Set(blockHash, Rlp.Encode(bal).Bytes);

    public void Insert(Hash256 blockHash, byte[] encodedBal)
        => balDb.Set(blockHash, encodedBal);

    // public void BulkInsert(IReadOnlyList<BlockHeader> headers)
    // {
    //     using IWriteBatch headerWriteBatch = headerDb.StartWriteBatch();
    //     using IWriteBatch blockNumberWriteBatch = blockNumberDb.StartWriteBatch();

    //     Span<byte> blockNumberSpan = stackalloc byte[8];
    //     foreach (BlockHeader header in headers)
    //     {
    //         using NettyRlpStream newRlp = _headerDecoder.EncodeToNewNettyStream(header);
    //         headerWriteBatch.Set(header.Number, header.Hash!, newRlp.AsSpan());

    //         header.Number.WriteBigEndian(blockNumberSpan);
    //         blockNumberWriteBatch.Set(header.Hash, blockNumberSpan);
    //     }
    // }

    public byte[]? GetRlp(Hash256 blockHash)
        => balDb.Get(blockHash);
    // {
    //     blockNumber ??= GetBlockNumberFromBlockNumberDb(blockHash);

    //     BlockHeader? header = null;
    //     if (blockNumber is not null)
    //     {
    //         header = headerDb.Get(blockNumber.Value, blockHash, _headerDecoder, _headerCache, shouldCache: shouldCache);
    //     }
    //     return header ?? headerDb.Get(blockHash, _headerDecoder, _headerCache, shouldCache: shouldCache);
    // }

    public BlockAccessList? Get(Hash256 blockHash)
    {
        byte[]? rlp = balDb.Get(blockHash);
        return rlp is null ? null : _balDecoder.Decode(rlp);
    }

    // public void Cache(BlockHeader header)
    // {
    //     _headerCache.Set(header.Hash, header);
    // }

    public void Delete(Hash256 blockHash)
        => balDb.Delete(blockHash);

    // public void InsertBlockNumber(Hash256 blockHash, long blockNumber)
    // {
    //     Span<byte> blockNumberSpan = stackalloc byte[8];
    //     blockNumber.WriteBigEndian(blockNumberSpan);
    //     blockNumberDb.Set(blockHash, blockNumberSpan);
    // }

    // public long? GetBlockNumber(Hash256 blockHash)
    // {
    //     long? blockNumber = GetBlockNumberFromBlockNumberDb(blockHash);
    //     if (blockNumber is not null) return blockNumber.Value;

    //     // Probably still hash based
    //     return Get(blockHash)?.Number;
    // }

    // private long? GetBlockNumberFromBlockNumberDb(Hash256 blockHash)
    // {
    //     Span<byte> numberSpan = blockNumberDb.GetSpan(blockHash);
    //     if (numberSpan.IsNullOrEmpty()) return null;
    //     try
    //     {
    //         if (numberSpan.Length != 8)
    //         {
    //             throw new InvalidDataException($"Unexpected number span length: {numberSpan.Length}");
    //         }

    //         return BinaryPrimitives.ReadInt64BigEndian(numberSpan);
    //     }
    //     finally
    //     {
    //         blockNumberDb.DangerousReleaseMemory(numberSpan);
    //     }
    // }

    // BlockHeader? IHeaderFinder.Get(Hash256 blockHash, long? blockNumber) => Get(blockHash, true, blockNumber);
}
