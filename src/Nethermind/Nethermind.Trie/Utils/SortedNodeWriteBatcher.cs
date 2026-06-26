// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Trie;

namespace Nethermind.Trie.Utils;

/// <summary>
/// Buffers concurrent trie-node writes, sorts them by their real node-storage key, and replays them in bounded batches.
/// </summary>
internal sealed class SortedNodeWriteBatcher(INodeStorage underlyingDb, int batchSize) : INodeStorage.IWriteBatch
{
    private readonly int _batchSize = Math.Max(1, batchSize);
    private readonly object _lock = new();
    private readonly List<NodeWrite> _writes = [];
    private long _sequence;
    private bool _disposed;

    public void Set(Hash256? address, in TreePath path, in ValueHash256 currentNodeKeccak, ReadOnlySpan<byte> data, WriteFlags writeFlags)
    {
        ValueHash256? storageAddress = address;
        byte[] storageKey = NodeStorage.GetNodeStoragePath(underlyingDb.Scheme, storageAddress, path, currentNodeKeccak);
        byte[]? dataCopy = data.IsNull() ? null : data.ToArray();

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _writes.Add(new NodeWrite(_sequence++, storageKey, address, path, currentNodeKeccak, dataCopy, writeFlags));
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _writes.Sort(NodeWriteComparer.Instance);
        for (int writeIndex = 0; writeIndex < _writes.Count;)
        {
            int endIndex = Math.Min(writeIndex + _batchSize, _writes.Count);
            using INodeStorage.IWriteBatch writeBatch = underlyingDb.StartWriteBatch();
            for (; writeIndex < endIndex; writeIndex++)
            {
                NodeWrite write = _writes[writeIndex];
                ReadOnlySpan<byte> replayData = write.Data is null ? default : write.Data;
                writeBatch.Set(write.Address, write.Path, write.Hash, replayData, write.WriteFlags);
            }
        }

        _writes.Clear();
    }

    private sealed class NodeWriteComparer : IComparer<NodeWrite>
    {
        public static NodeWriteComparer Instance { get; } = new();

        public int Compare(NodeWrite x, NodeWrite y)
        {
            int result = x.StorageKey.AsSpan().SequenceCompareTo(y.StorageKey);
            return result != 0 ? result : x.Sequence.CompareTo(y.Sequence);
        }
    }

    private readonly record struct NodeWrite(
        long Sequence,
        byte[] StorageKey,
        Hash256? Address,
        TreePath Path,
        ValueHash256 Hash,
        byte[]? Data,
        WriteFlags WriteFlags);
}
