// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Facade.Simulate;

public class SimulateDictionaryBlockStore(IBlockStore readonlyBaseBlockStore) : IBlockStore
{
    private readonly Dictionary<Hash256AsKey, Block> _blockDict = [];
    private readonly Dictionary<long, Block> _blockNumDict = [];
    private readonly BlockDecoder _blockDecoder = new();

    public void Insert(Block block, WriteFlags writeFlags = WriteFlags.None)
    {
        _blockDict[block.Hash] = block;
        _blockNumDict[block.Number] = block;
    }

    public void Delete(long blockNumber, Hash256 blockHash)
    {
        _blockDict.Remove(blockHash);
        _blockNumDict.Remove(blockNumber);
    }

    public Block? Get(long blockNumber, Hash256 blockHash, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool shouldCache = true)
    {
        if (_blockNumDict.TryGetValue(blockNumber, out Block block))
        {
            return block;
        }

        block = readonlyBaseBlockStore.Get(blockNumber, blockHash, rlpBehaviors, false);
        if (block is not null && shouldCache)
        {
            Cache(block);
        }
        return block;
    }

    public byte[]? GetRlp(long blockNumber, Hash256 blockHash)
    {
        if (_blockNumDict.TryGetValue(blockNumber, out Block block))
        {
            using NettyRlpStream newRlp = _blockDecoder.EncodeToNewNettyStream(block);
            return newRlp.AsSpan().ToArray();
        }
        return readonlyBaseBlockStore.GetRlp(blockNumber, blockHash);
    }

    public ReceiptRecoveryBlock? GetReceiptRecoveryBlock(long blockNumber, Hash256 blockHash)
    {
        if (_blockNumDict.TryGetValue(blockNumber, out Block block))
        {
            using NettyRlpStream newRlp = _blockDecoder.EncodeToNewNettyStream(block);
            using var memoryManager = new CappedArrayMemoryManager(newRlp.Data);
            return BlockDecoder.DecodeToReceiptRecoveryBlock(memoryManager, memoryManager.Memory, RlpBehaviors.None);
        }
        return readonlyBaseBlockStore.GetReceiptRecoveryBlock(blockNumber, blockHash);
    }

    public void Cache(Block block)
        => Insert(block);

    public bool HasBlock(long blockNumber, Hash256 blockHash)
        => _blockNumDict.ContainsKey(blockNumber);

    public IEnumerable<Block> GetBlocksOlderThan(ulong timestamp)
    {
        foreach (KeyValuePair<long, Block> kv in _blockNumDict)
        {
            if (kv.Value.Timestamp >= timestamp)
            {
                continue;
            }
            yield return kv.Value;
        }
    }

    public Block? GetBlockByTimestamp(ulong timestamp)
    {
        foreach (KeyValuePair<long, Block> kv in _blockNumDict)
        {
            if (kv.Value.Timestamp == timestamp)
            {
                return kv.Value;
            }
        }

        return null;
    }
}
