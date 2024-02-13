// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Collections.EliasFano;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.EliasFano;

namespace Nethermind.Verkle.Tree.History.V2;

public class HistoryOfAccounts
{
    private static readonly EliasFanoDecoder _decoder = new();
    private readonly IDb _historyOfAccounts;

    public HistoryOfAccounts(IDb historyOfAccounts)
    {
        _historyOfAccounts = historyOfAccounts;
    }

    public int BlocksChunks { get; set; } = 2000;

    public void AppendHistoryBlockNumberForKey(Hash256 key, ulong blockNumber)
    {
        List<ulong> shard = GetLastShardOfBlocks(key);
        // Console.WriteLine($"AppendHistoryBlockNumberForKey: {key} {blockNumber} LastShard:{string.Join(",", shard)}");
        shard.Add(blockNumber);
        InsertShard(key, shard);
    }

    private void InsertShard(Hash256 key, List<ulong> shard)
    {
        EliasFano ef;
        try
        {
            var universe = shard[^1] + 1;
            EliasFanoBuilder efb = new(universe, shard.Count);
            efb.Extend(shard);
            ef = efb.Build();
        }
        catch (EliasFanoBuilderException e)
        {
            throw new EliasFanoBuilderException(
                $"trying to create from shard and failed key:{key} shard:[{string.Join(", ", shard)}]",
                e)
            { Shard = shard };
        }

        RlpStream streamNew = new(_decoder.GetLength(ef, RlpBehaviors.None));
        _decoder.Encode(streamNew, ef);
        if (shard.Count == BlocksChunks)
        {
            HistoryKey historyKey = new(key, shard[^1]);
            _historyOfAccounts[historyKey.Encode()] = streamNew.Data.ToArray();
            historyKey = new HistoryKey(key, ulong.MaxValue);
            _historyOfAccounts[historyKey.Encode()] = Array.Empty<byte>();
        }
        else
        {
            var historyKey = new HistoryKey(key, ulong.MaxValue);
            _historyOfAccounts[historyKey.Encode()] = streamNew.Data.ToArray();
        }
    }

    private void InsertShards(Hash256 key, List<List<ulong>> shardsList)
    {
        foreach (List<ulong> shard in shardsList) InsertShard(key, shard);
    }

    private List<ulong> GetLastShardOfBlocks(Hash256 key)
    {
        var shardKey = new HistoryKey(key, ulong.MaxValue);
        var ef = _historyOfAccounts[shardKey.Encode()];
        List<ulong> shard = new();
        if (ef is not null && ef.Length != 0)
        {
            EliasFano eliasFano = _decoder.Decode(new RlpStream(ef));
            shard.AddRange(eliasFano.GetEnumerator(0));
        }

        return shard;
    }

    public EliasFano? GetAppropriateShard(Hash256 key, ulong blockNumber)
    {
        HistoryKey historyKey = new(key, blockNumber);
        IEnumerable<KeyValuePair<byte[], byte[]?>> itr = _historyOfAccounts.GetIterator(historyKey.Encode());
        KeyValuePair<byte[], byte[]?> keyVal = itr.FirstOrDefault();
        // Console.WriteLine($"BN:{blockNumber} HK:{historyKey.Encode().ToHexString()} GHK:{keyVal.Key.ToHexString()}");
        return keyVal.Key is not null && keyVal.Value is not null && keyVal.Value.Length != 0
            ? _decoder.Decode(new RlpStream(keyVal.Value!))
            : null;
    }
}

public readonly struct HistoryKey
{
    public Hash256 Key { get; }
    public ulong MaxBlock { get; }

    public HistoryKey(Hash256 address, ulong maxBlock)
    {
        Key = address;
        MaxBlock = maxBlock;
    }

    public byte[] Encode()
    {
        var data = new byte[40];
        Span<byte> dataSpan = data;
        Key.Bytes.CopyTo(dataSpan);
        BinaryPrimitives.WriteUInt64BigEndian(dataSpan.Slice(32), MaxBlock);
        return data;
    }
}
