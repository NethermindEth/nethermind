// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.LightClient;

/// <summary>A complete authenticated storage trie whose sealed nodes are shared by subsequent snapshots.</summary>
internal sealed class HostedStorageSnapshot
{
    private readonly PatriciaTree _tree;

    private HostedStorageSnapshot(PatriciaTree tree, int count)
    {
        _tree = tree;
        Count = count;
    }

    internal Hash256 RootHash => _tree.RootHash;
    internal int Count { get; }

    internal static HostedStorageSnapshot Create(Hash256 root, IReadOnlyDictionary<ValueHash256, UInt256> storage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PatriciaTree tree = new();
        foreach ((ValueHash256 key, UInt256 value) in storage)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value.IsZero) throw new InvalidDataException("A storage snapshot contains a zero-valued leaf.");
            tree.Set(key.Bytes, Rlp.Encode(value).Bytes);
        }
        return Complete(tree, storage.Count, root, cancellationToken);
    }

    internal bool TryGetValue(ValueHash256 key, out UInt256 value)
    {
        ReadOnlySpan<byte> encoded = _tree.Get(key.Bytes);
        if (encoded.IsEmpty)
        {
            value = UInt256.Zero;
            return false;
        }
        RlpReader reader = new(encoded);
        value = reader.DecodeUInt256();
        return true;
    }

    /// <summary>Finds an exact node boundary in the complete, sealed snapshot graph.</summary>
    internal bool TryGetNode(TreePath path, [NotNullWhen(true)] out TrieNode? node)
    {
        node = _tree.RootRef;
        TreePath cursor = TreePath.Empty;
        while (node is not null && cursor.Length < path.Length)
        {
            int child;
            if (node.IsBranch)
            {
                child = path[cursor.Length];
                cursor.AppendMut(child);
            }
            else if (node.IsExtension)
            {
                byte[] key = node.Key!;
                if (key.Length > path.Length - cursor.Length) break;
                for (int i = 0; i < key.Length; i++)
                    if (path[cursor.Length + i] != key[i])
                    {
                        node = null;
                        return false;
                    }
                cursor.AppendMut(key);
                child = 0;
            }
            else break;
            node = node.GetChildWithChildPath(_tree.TrieStore, ref cursor, child, keepChildRef: true);
        }
        if (node is not null && cursor.Length == path.Length) return true;
        node = null;
        return false;
    }

    internal HostedStorageSnapshot Apply(IReadOnlyDictionary<ValueHash256, UInt256> changes, Hash256 targetRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PatriciaTree tree = new() { RootRef = _tree.RootRef };
        int count = Count;
        foreach ((ValueHash256 key, UInt256 value) in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool exists = !tree.Get(key.Bytes).IsEmpty;
            if (value.IsZero)
            {
                if (!exists) continue;
                count--;
            }
            else if (!exists) count++;
            tree.Set(key.Bytes, value.IsZero ? [] : Rlp.Encode(value).Bytes);
        }
        return Complete(tree, count, targetRoot, cancellationToken);
    }

    private static HostedStorageSnapshot Complete(PatriciaTree tree, int count, Hash256 targetRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        tree.UpdateRootHash();
        if (tree.RootHash != targetRoot)
            throw new InvalidDataException("Hosted storage is incomplete or does not match its authenticated root.");
        cancellationToken.ThrowIfCancellationRequested();
        TrieNode? root = tree.RootRef;
        tree.Commit();
        // The in-memory graph is its own backing storage; Commit normally replaces the root with a database reference.
        tree.RootRef = root;
        cancellationToken.ThrowIfCancellationRequested();
        return new(tree, count);
    }
}
