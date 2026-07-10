// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.Blockchain.BlockAccessLists;

public class BlockAccessListStore(
    [KeyFilter(DbNames.BlockAccessLists)] IDb balDb,
    BlockAccessListDecoder? decoder = null)
    : IBlockAccessListStore
{
    private const int KeyLength = 40;

    private readonly BlockAccessListDecoder _balDecoder = decoder ?? new();

    [SkipLocalsInit]
    public void Insert(ulong blockNumber, Hash256 blockHash, ReadOnlyBlockAccessList bal)
    {
        using ArrayPoolSpan<byte> rlp = _balDecoder.EncodeToArrayPoolSpan(bal);
        Span<byte> key = stackalloc byte[KeyLength];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, key);
        balDb.PutSpan(key, rlp);
    }

    [SkipLocalsInit]
    public void Insert(ulong blockNumber, Hash256 blockHash, byte[] encodedBal)
    {
        Span<byte> key = stackalloc byte[KeyLength];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, key);
        balDb.PutSpan(key, encodedBal);
    }

    [SkipLocalsInit]
    public void Insert(ulong blockNumber, Hash256 blockHash, scoped ReadOnlySpan<byte> encodedBal)
    {
        Span<byte> key = stackalloc byte[KeyLength];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, key);
        balDb.PutSpan(key, encodedBal);
    }

    [SkipLocalsInit]
    public MemoryManager<byte>? GetRlp(ulong blockNumber, Hash256 blockHash)
    {
        Span<byte> key = stackalloc byte[KeyLength];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, key);
        return balDb.GetOwnedMemory(key);
    }

    [SkipLocalsInit]
    public bool Exists(ulong blockNumber, Hash256 blockHash)
    {
        Span<byte> key = stackalloc byte[KeyLength];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, key);
        return balDb.KeyExists(key);
    }

    public ReadOnlyBlockAccessList? Get(ulong blockNumber, Hash256 blockHash)
    {
        using MemoryManager<byte>? rlp = GetRlp(blockNumber, blockHash);
        return rlp is null ? null : _balDecoder.Decode(rlp.Memory.Span);
    }

    [SkipLocalsInit]
    public void Delete(ulong blockNumber, Hash256 blockHash)
    {
        Span<byte> key = stackalloc byte[KeyLength];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, key);
        balDb.Remove(key);
    }
}
