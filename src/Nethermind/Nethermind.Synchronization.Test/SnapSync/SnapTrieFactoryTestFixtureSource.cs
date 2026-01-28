// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Sync;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

public class SnapTrieFactoryTestFixtureSource : IEnumerable
{
    public static ISnapTrieFactory CreatePatriciaFactory(INodeStorage nodeStorage, ILogManager logManager) =>
        new PatriciaSnapTrieFactory(nodeStorage, logManager);

    public static ISnapTrieFactory CreateFlatFactory(INodeStorage nodeStorage, ILogManager logManager) =>
        new FlatSnapTrieFactory(new NodeStoragePersistenceAdapter(nodeStorage), logManager);

    public IEnumerator GetEnumerator()
    {
        yield return new TestFixtureData((Func<INodeStorage, ILogManager, ISnapTrieFactory>)CreatePatriciaFactory)
            .SetArgDisplayNames("Patricia");
        yield return new TestFixtureData((Func<INodeStorage, ILogManager, ISnapTrieFactory>)CreateFlatFactory)
            .SetArgDisplayNames("Flat");
    }

    /// <summary>
    /// IPersistence adapter that wraps INodeStorage for testing.
    /// Maintains shared state across all readers/writers to simulate real persistence.
    /// </summary>
    private sealed class NodeStoragePersistenceAdapter(INodeStorage nodeStorage) : IPersistence
    {
        // Track written state trie paths to allow IsPersisted checks across trees
        private readonly ConcurrentDictionary<TreePath, byte> _writtenStatePaths = new();
        // Track written storage trie paths per address
        private readonly ConcurrentDictionary<(Hash256, TreePath), byte> _writtenStoragePaths = new();

        public IPersistence.IPersistenceReader CreateReader() =>
            new NodeStorageReader(_writtenStatePaths, _writtenStoragePaths);

        public IPersistence.IWriteBatch CreateWriteBatch(StateId from, StateId to, WriteFlags flags = WriteFlags.None) =>
            new NodeStorageWriteBatch(nodeStorage, _writtenStatePaths, _writtenStoragePaths);

        public void Flush() { }
    }

    /// <summary>
    /// Reader that checks shared written paths to determine if state is persisted.
    /// </summary>
    private sealed class NodeStorageReader(
        ConcurrentDictionary<TreePath, byte> writtenStatePaths,
        ConcurrentDictionary<(Hash256, TreePath), byte> writtenStoragePaths) : IPersistence.IPersistenceReader
    {
        public void Dispose() { }
        public Account? GetAccount(Address address) => null;
        public bool TryGetSlot(Address address, in Int256.UInt256 slot, ref SlotValue outValue) => false;
        public StateId CurrentState => default;

        // Return non-null if the path was written (simulates persisted state)
        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) =>
            writtenStatePaths.ContainsKey(path) ? [] : null;

        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) =>
            writtenStoragePaths.ContainsKey((address, path)) ? [] : null;

        public byte[]? GetAccountRaw(Hash256 addrHash) => null;
        public bool TryGetStorageRaw(Hash256 addrHash, Hash256 slotHash, ref SlotValue value) => false;
        public IPersistence.IFlatIterator CreateAccountIterator() => throw new NotSupportedException();
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey) => throw new NotSupportedException();
        public bool IsPreimageMode => false;
    }

    /// <summary>
    /// Write batch adapter that writes trie nodes to INodeStorage and tracks written paths.
    /// </summary>
    private sealed class NodeStorageWriteBatch(
        INodeStorage nodeStorage,
        ConcurrentDictionary<TreePath, byte> writtenStatePaths,
        ConcurrentDictionary<(Hash256, TreePath), byte> writtenStoragePaths) : IPersistence.IWriteBatch
    {
        public void Dispose() { }
        public void SelfDestruct(Address addr) { }
        public void SetAccount(Address addr, Account? account) { }
        public void SetStorage(Address addr, in Int256.UInt256 slot, in SlotValue? value) { }
        public void SetStorageRaw(Hash256 addrHash, Hash256 slotHash, in SlotValue? value) { }
        public void SetAccountRaw(Hash256 addrHash, Account account) { }

        public void SetStateTrieNode(in TreePath path, TrieNode node)
        {
            TreePath mutablePath = path;
            node.ResolveKey(NullTrieNodeResolver.Instance, ref mutablePath);
            if (node.Keccak is not null)
            {
                nodeStorage.Set(null, path, node.Keccak, node.FullRlp.Span);
                writtenStatePaths.TryAdd(path, 0);
            }
        }

        public void SetStorageTrieNode(Hash256 address, in TreePath path, TrieNode node)
        {
            TreePath mutablePath = path;
            node.ResolveKey(NullTrieNodeResolver.Instance, ref mutablePath);
            if (node.Keccak is not null)
            {
                nodeStorage.Set(address, path, node.Keccak, node.FullRlp.Span);
                writtenStoragePaths.TryAdd((address, path), 0);
            }
        }
    }
}
