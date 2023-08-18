// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

/// <summary>
/// Track every rented CappedArray<byte> and return them all at once
/// </summary>
public class TrackedPooledBufferTrieStore: ITrieStore
{
    private ITrieStore _baseImplementation;
    private List<CappedArray<byte>> _rentedBuffers = new();

    public TrackedPooledBufferTrieStore(ITrieStore baseImplementation)
    {
        _baseImplementation = baseImplementation;
    }

    public TrieNode FindCachedOrUnknown(Keccak hash)
    {
        return _baseImplementation.FindCachedOrUnknown(hash);
    }

    public byte[]? LoadRlp(Keccak hash, ReadFlags flags = ReadFlags.None)
    {
        return _baseImplementation.LoadRlp(hash, flags);
    }

    public void Dispose()
    {
        _baseImplementation.Dispose();
    }

    public void CommitNode(long blockNumber, NodeCommitInfo nodeCommitInfo, WriteFlags writeFlags = WriteFlags.None)
    {
        _baseImplementation.CommitNode(blockNumber, nodeCommitInfo, writeFlags);
    }

    public void FinishBlockCommit(TrieType trieType, long blockNumber, TrieNode? root, WriteFlags writeFlags = WriteFlags.None)
    {
        _baseImplementation.FinishBlockCommit(trieType, blockNumber, root, writeFlags);
    }

    public bool IsPersisted(in ValueKeccak keccak)
    {
        return _baseImplementation.IsPersisted(in keccak);
    }

    public IReadOnlyTrieStore AsReadOnly(IKeyValueStore? keyValueStore)
    {
        return _baseImplementation.AsReadOnly(keyValueStore);
    }

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => _baseImplementation.ReorgBoundaryReached += value;
        remove => _baseImplementation.ReorgBoundaryReached -= value;
    }

    public IKeyValueStore AsKeyValueStore()
    {
        return _baseImplementation.AsKeyValueStore();
    }

    public CappedArray<byte> RentBuffer(int size)
    {
        var rented = new CappedArray<byte>(ArrayPool<byte>.Shared.Rent(size), size);
        _rentedBuffers.Add(rented);
        return rented;
    }

    public void ReturnBuffer(CappedArray<byte> buffer)
    {
    }

    public void ReturnAll()
    {
        foreach (CappedArray<byte> rentedBuffer in _rentedBuffers)
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer.Array);
        }
    }
}
