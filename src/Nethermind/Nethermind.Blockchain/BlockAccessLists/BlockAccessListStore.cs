// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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
    private readonly BlockAccessListDecoder _balDecoder = decoder ?? new();

    public void Insert(Hash256 blockHash, BlockAccessList bal)
        => balDb.Set(blockHash, Rlp.Encode(bal).Bytes);

    public void Insert(Hash256 blockHash, byte[] encodedBal)
        => balDb.Set(blockHash, encodedBal);

    public byte[]? GetRlp(Hash256 blockHash)
        => balDb.Get(blockHash);

    public BlockAccessList? Get(Hash256 blockHash)
    {
        byte[]? rlp = balDb.Get(blockHash);
        return rlp is null ? null : _balDecoder.Decode(rlp);
    }

    public void Delete(Hash256 blockHash)
        => balDb.Delete(blockHash);
}
