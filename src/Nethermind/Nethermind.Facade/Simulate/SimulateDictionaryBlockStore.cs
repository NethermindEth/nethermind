// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Facade.Simulate;

public class SimulateDictionaryBlockStore : IBlockStore
{
    public class StructuralByteArrayComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[] x, byte[] y)
        {
            return StructuralComparisons.StructuralEqualityComparer.Equals(x, y);
        }

        public int GetHashCode(byte[] obj)
        {
            return StructuralComparisons.StructuralEqualityComparer.GetHashCode(obj);
        }
    }


    public class CappedArrayMemoryManager : MemoryManager<byte>
    {
        private readonly CappedArray<byte> _data;
        private bool _isDisposed;

        public CappedArrayMemoryManager(CappedArray<byte>? data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _isDisposed = false;
        }

        public override Span<byte> GetSpan()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(CappedArrayMemoryManager));
            }

            return _data.AsSpan();
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(CappedArrayMemoryManager));
            }

            if (elementIndex < 0 || elementIndex >= _data.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(elementIndex));
            }
            // Pinning is a no-op in this managed implementation
            return new MemoryHandle();
        }

        public override void Unpin()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(CappedArrayMemoryManager));
            }
            // Unpinning is a no-op in this managed implementation
        }

        protected override void Dispose(bool disposing)
        {
            _isDisposed = true;
        }

        protected override bool TryGetArray(out ArraySegment<byte> segment)
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(CappedArrayMemoryManager));
            }

            segment = new ArraySegment<byte>(_data.ToArray() ?? throw new InvalidOperationException());
            return true;
        }
    }

    private readonly IBlockStore _readonlyBaseBlockStore;
    private readonly Dictionary<ValueHash256, Block> _blockDict = new();
    private readonly Dictionary<long, Dictionary<Hash256, Block>> _blockNumDict = new();
    private readonly Dictionary<byte[], byte[]> _metadataDict = new(new StructuralByteArrayComparer());
    private readonly BlockDecoder _blockDecoder = new();

    public SimulateDictionaryBlockStore(IBlockStore readonlyBaseBlockStore)
    {
        _readonlyBaseBlockStore = readonlyBaseBlockStore;
    }

    public void Insert(Block block, WriteFlags writeFlags = WriteFlags.None)
    {
        _blockDict[block.Hash] = block;
        if (!_blockNumDict.ContainsKey(block.Number))
        {
            _blockNumDict[block.Number] = new Dictionary<Hash256, Block>();
        }
        _blockNumDict[block.Number][block.Hash] = block;
    }

    public void Delete(long blockNumber, Hash256 blockHash)
    {
        _blockDict.Remove(blockHash);
        if (_blockNumDict.ContainsKey(blockNumber))
        {
            _blockNumDict[blockNumber].Remove(blockHash);
            if (_blockNumDict[blockNumber].Count == 0)
            {
                _blockNumDict.Remove(blockNumber);
            }
        }
        _readonlyBaseBlockStore.Delete(blockNumber, blockHash);
    }

    public Block? Get(long blockNumber, Hash256 blockHash, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool shouldCache = true)
    {
        if (_blockNumDict.TryGetValue(blockNumber, out var blockDict) && blockDict.TryGetValue(blockHash, out var block))
        {
            return block;
        }

        block = _readonlyBaseBlockStore.Get(blockNumber, blockHash, rlpBehaviors, shouldCache);
        if (block != null && shouldCache)
        {
            Cache(block);
        }
        return block;
    }

    public byte[]? GetRaw(long blockNumber, Hash256 blockHash)
    {
        if (_blockNumDict.TryGetValue(blockNumber, out var blockDict) && blockDict.TryGetValue(blockHash, out var block))
        {
            using NettyRlpStream newRlp = _blockDecoder.EncodeToNewNettyStream(block);
            return newRlp.AsSpan().ToArray();
        }
        return _readonlyBaseBlockStore.GetRaw(blockNumber, blockHash);
    }

    public IEnumerable<Block> GetAll()
    {
        var allBlocks = new HashSet<Block>(_readonlyBaseBlockStore.GetAll());
        foreach (var block in _blockDict.Values)
        {
            allBlocks.Add(block);
        }
        return allBlocks;
    }

    public ReceiptRecoveryBlock? GetReceiptRecoveryBlock(long blockNumber, Hash256 blockHash)
    {
        if (_blockNumDict.TryGetValue(blockNumber, out var blockDict) && blockDict.TryGetValue(blockHash, out var block))
        {
            using NettyRlpStream newRlp = _blockDecoder.EncodeToNewNettyStream(block);
            using var memoryManager = new CappedArrayMemoryManager(newRlp.Data);
            return BlockDecoder.DecodeToReceiptRecoveryBlock(memoryManager, memoryManager.Memory, RlpBehaviors.None);
        }
        return _readonlyBaseBlockStore.GetReceiptRecoveryBlock(blockNumber, blockHash);
    }

    public void Cache(Block block)
    {
        Insert(block);
    }

    public void SetMetadata(byte[] key, byte[] value)
    {
        _metadataDict[key] = value;
        _readonlyBaseBlockStore.SetMetadata(key, value);
    }

    public byte[]? GetMetadata(byte[] key)
    {
        if (_metadataDict.TryGetValue(key, out var value))
        {
            return value;
        }
        return _readonlyBaseBlockStore.GetMetadata(key);
    }
}
