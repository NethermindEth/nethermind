// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Facade.Simulate;

public class SimulateDictionaryBlockStore(IBlockStore readonlyBaseBlockStore) : IBlockStore
{
    private readonly Dictionary<Hash256AsKey, Block> _blockDict = [];
    private readonly Dictionary<ulong, Block> _blockNumDict = [];
    private readonly BlockDecoder _blockDecoder = new();

    public void Insert(Block block, WriteFlags writeFlags = WriteFlags.None)
    {
        _blockDict[block.Hash] = block;
        _blockNumDict[block.Number] = block;
    }

    public void Delete(ulong blockNumber, Hash256 blockHash)
    {
        _blockDict.Remove(blockHash);
        _blockNumDict.Remove(blockNumber);
    }

    public Block? Get(ulong blockNumber, Hash256 blockHash, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool shouldCache = true)
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

    public byte[]? GetRlp(ulong blockNumber, Hash256 blockHash)
    {
        if (_blockNumDict.TryGetValue(blockNumber, out Block block))
        {
            return _blockDecoder.EncodeAsBytes(block);
        }
        return readonlyBaseBlockStore.GetRlp(blockNumber, blockHash);
    }

    public ReceiptRecoveryBlock? GetReceiptRecoveryBlock(ulong blockNumber, Hash256 blockHash) =>
        _blockNumDict.TryGetValue(blockNumber, out Block block)
            ? new ReceiptRecoveryBlock(block)
            : readonlyBaseBlockStore.GetReceiptRecoveryBlock(blockNumber, blockHash);

    public void Cache(Block block)
        => Insert(block);

    public bool HasBlock(ulong blockNumber, Hash256 blockHash)
        => _blockNumDict.ContainsKey(blockNumber);
}
