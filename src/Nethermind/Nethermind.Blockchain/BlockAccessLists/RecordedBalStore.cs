// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.Blockchain.Headers;

public class RecordedBalStore([KeyFilter(DbNames.RecordedBal)] IDb db) : IRecordedBalStore
{
    public void Insert(Block block, BlockAccessList bal)
    {
        using NettyRlpStream rlpStream = BlockAccessListDecoder.Instance.EncodeToNewNettyStream(bal);
        Span<byte> key = stackalloc byte[40];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(block.Number, block.Hash, key);
        db.PutSpan(key, rlpStream.AsSpan());
    }

    public BlockAccessList? Get(long blockNumber, Hash256 blockHash)
    {
        Span<byte> key = stackalloc byte[40];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, key);
        ReadOnlySpan<byte> rlp = db.GetSpan(key);
        return rlp.IsEmpty ? null : BlockAccessListDecoder.Instance.Decode(rlp);
    }
}

public class NullRecordedBalStore : IRecordedBalStore
{
    public static NullRecordedBalStore Instance { get; } = new();
    public void Insert(Block block, BlockAccessList bal) { }
    public BlockAccessList? Get(long blockNumber, Hash256 blockHash) => null;
}
