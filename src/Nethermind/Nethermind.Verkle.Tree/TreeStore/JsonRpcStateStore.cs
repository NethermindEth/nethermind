// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Verkle.Tree.History.V1;
using Nethermind.Verkle.Tree.TreeNodes;
using Nethermind.Verkle.Tree.VerkleDb;

namespace Nethermind.Verkle.Tree.TreeStore;

/// <summary>
///     This is a state store designed to be used with JsonRpc
///     this will use the VerkleStateStore and VerkleArchiveStore to provide historical data for JsonRpc calls
/// </summary>
public class JsonRpcStateStore
{
    public static Span<byte> RootNodeKey => Array.Empty<byte>();
    private readonly VerkleArchiveStore _archiveStore;
    private readonly VerkleMemoryDb _keyValueStore;
    private readonly IVerkleTreeStore _verkleStateStore;

    public JsonRpcStateStore(VerkleArchiveStore archiveStore, IVerkleTreeStore verkleStateStore,
        VerkleMemoryDb keyValueStore)
    {
        _archiveStore = archiveStore;
        _verkleStateStore = verkleStateStore;
        _keyValueStore = keyValueStore;
        StateStoreStateRoot = _verkleStateStore.StateRoot;
    }

    private Hash256 StateStoreStateRoot { get; set; }

    public Hash256 StateRoot
    {
        get
        {
            _keyValueStore.GetInternalNode(RootNodeKey, out InternalNode? value);
            return value is null ? StateStoreStateRoot : new Hash256(value.Bytes);
        }
    }

    public byte[]? GetLeaf(ReadOnlySpan<byte> key, Hash256? stateRoot = null)
    {
        try
        {
            if (!_keyValueStore.GetLeaf(key, out var value))
                value = _verkleStateStore.GetLeaf(key, stateRoot ?? StateStoreStateRoot);
            return value;
        }
        catch (StateUnavailableExceptions)
        {
            return _archiveStore.GetLeaf(key, stateRoot!);
        }
    }

    public InternalNode? GetInternalNode(ReadOnlySpan<byte> key, Hash256? stateRoot = null)
    {
        return _keyValueStore.GetInternalNode(key, out InternalNode? value)
            ? value
            : _verkleStateStore.GetInternalNode(key, stateRoot ?? StateStoreStateRoot);
    }

    public void SetLeaf(ReadOnlySpan<byte> leafKey, byte[] leafValue)
    {
        _keyValueStore.SetLeaf(leafKey, leafValue);
    }

    public void SetInternalNode(ReadOnlySpan<byte> internalNodeKey, InternalNode internalNodeValue)
    {
        _keyValueStore.SetInternalNode(internalNodeKey, internalNodeValue);
    }

    public void InsertBatch(long blockNumber, VerkleMemoryDb batch)
    {
    }

    public void MoveToStateRoot(Hash256 stateRoot)
    {
        StateStoreStateRoot = stateRoot;
    }
}
