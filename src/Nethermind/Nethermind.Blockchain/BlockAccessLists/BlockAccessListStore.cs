// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;
using System;

namespace Nethermind.Blockchain.Headers;

public class BlockAccessListStore(
    [KeyFilter(DbNames.BlockAccessLists)] IDb balDb,
    BlockAccessListDecoder? decoder = null)
    : IBlockAccessListStore
{
    private readonly BlockAccessListDecoder _balDecoder = decoder ?? new();

    public void Insert(Hash256 blockHash, BlockAccessList bal)
    {
        using NettyRlpStream rlpStream = BlockAccessListDecoder.Instance.EncodeToNewNettyStream(bal);
        balDb.Set(blockHash, rlpStream.AsSpan());
    }

    public void Insert(Hash256 blockHash, byte[] encodedBal)
        => balDb.Set(blockHash, encodedBal);

    public byte[]? GetRlp(Hash256 blockHash)
        => balDb.Get(blockHash);

    public BlockAccessList? Get(Hash256 blockHash)
    {
        ReadOnlySpan<byte> rlp = balDb.GetSpan(blockHash);
        return rlp.IsEmpty ? null : _balDecoder.Decode(rlp);
    }

    public void Delete(Hash256 blockHash)
        => balDb.Delete(blockHash);
}
