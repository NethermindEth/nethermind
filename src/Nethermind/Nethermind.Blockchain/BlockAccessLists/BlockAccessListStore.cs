// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
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
    private readonly BlockAccessListDecoder _balDecoder = decoder ?? new();

    public void Insert(Hash256 blockHash, ReadOnlyBlockAccessList bal)
    {
        using NettyRlpStream rlpStream = BlockAccessListDecoder.Instance.EncodeToNewNettyStream(bal);
        balDb.Set(blockHash, rlpStream.AsSpan());
    }

    public void Insert(Hash256 blockHash, byte[] encodedBal)
        => balDb.Set(blockHash, encodedBal);

    public void Insert(Hash256 blockHash, ReadOnlySpan<byte> encodedBal)
        => balDb.PutSpan(blockHash.Bytes, encodedBal);

    public MemoryManager<byte>? GetRlp(Hash256 blockHash)
        => balDb.GetOwnedMemory(blockHash.Bytes);

    public bool Exists(Hash256 blockHash)
        => balDb.KeyExists(blockHash);

    public ReadOnlyBlockAccessList? Get(Hash256 blockHash)
    {
        using MemoryManager<byte>? rlp = GetRlp(blockHash);
        return rlp is null ? null : _balDecoder.Decode(rlp.Memory.Span);
    }

    public void Delete(Hash256 blockHash)
        => balDb.Delete(blockHash);
}
