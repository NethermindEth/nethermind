// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.Synchronization.FastSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.Sync;

/// <summary>
/// Rebuilds a trie's missing internal nodes locally from the disconnected subtrees that snap sync left behind,
/// avoiding the network round-trips of post-snap state healing.
/// </summary>
/// <remarks>
/// Algorithm: from the (potentially missing) root, probe the path-keyed trie node store. If a node exists at
/// the current path it is reused as-is; otherwise the 16 child slots are explored. Each slot is "occupied" iff
/// some flat account/storage leaf with a hash extending through that nibble exists. Recursion descends until a
/// stored trie node is found or a slot proves empty. When a synthesized branch ends up with only one occupied
/// slot it is collapsed: a Branch child becomes a one-nibble Extension; a Leaf/Extension child gets its key
/// prefixed with the slot nibble (avoiding illegal Extension→Extension chains).
///
/// State and storage tries are handled the same way. To bridge the two, callers can pass a
/// <c>storageRootRewrites</c> map; whenever a state leaf is encountered whose hash matches an entry, the leaf's
/// <see cref="Account.StorageRoot"/> is rewritten before re-hashing so the parent branches end up referencing
/// the freshly assembled storage root.
/// </remarks>
public sealed class TrieReassembler(IPersistence persistence, ILogManager logManager) : ITrieReassembler
{
    private readonly ILogger _logger = logManager.GetClassLogger<TrieReassembler>();
    private readonly AccountDecoder _accountDecoder = AccountDecoder.Instance;

    private const int MaxPathLength = 64;
    private const int BranchChildCount = 16;

    /// <inheritdoc/>
    /// <remarks>
    /// Reassembles each storage trie listed in <paramref name="updatedStorageAccounts"/> first (collecting
    /// the new <c>StorageRoot</c> per account), then reassembles the state trie while rewriting state-leaf
    /// <c>Account.StorageRoot</c> entries to point at the freshly assembled storage roots.
    /// </remarks>
    public Hash256? TryReassemble(IReadOnlyCollection<Hash256> updatedStorageAccounts)
    {
        Dictionary<ValueHash256, Hash256> rewrites = new(updatedStorageAccounts.Count);
        foreach (Hash256 accountHash in updatedStorageAccounts)
        {
            Hash256? newRoot = ReassembleStorageTrie(accountHash.ValueHash256);
            if (newRoot is not null)
            {
                rewrites[accountHash.ValueHash256] = newRoot;
            }
        }

        if (_logger.IsInfo) _logger.Info($"Trie reassembly: rebuilt {rewrites.Count}/{updatedStorageAccounts.Count} storage tries; rebuilding state.");

        return ReassembleStateTrie(rewrites);
    }

    /// <summary>
    /// Reassemble the state trie. If <paramref name="storageRootRewrites"/> is provided, leaves matching an entry
    /// will have their <see cref="Account.StorageRoot"/> rewritten to the corresponding hash.
    /// </summary>
    /// <returns>The root hash of the reassembled trie, or <see langword="null"/> if the DB has no leaves at all.</returns>
    public Hash256? ReassembleStateTrie(IReadOnlyDictionary<ValueHash256, Hash256>? storageRootRewrites = null)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);

        TreePath path = TreePath.Empty;
        SubtreeResult? result = Reassemble(reader, batch, address: null, ref path, storageRootRewrites);
        return result?.Hash;
    }

    /// <summary>
    /// Reassemble the storage trie of a single account.
    /// </summary>
    /// <returns>The root hash of the reassembled storage trie, or <see langword="null"/> if the storage has no slots.</returns>
    public Hash256? ReassembleStorageTrie(in ValueHash256 accountHash)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);

        Hash256 address = new(accountHash);
        TreePath path = TreePath.Empty;
        SubtreeResult? result = Reassemble(reader, batch, address: address, ref path, storageRootRewrites: null);
        return result?.Hash;
    }

    private readonly record struct SubtreeResult(
        Hash256 Hash,
        NodeType Type,
        byte[]? Key,
        Hash256? InnerHash,
        byte[]? Value);

    private SubtreeResult? Reassemble(
        IPersistence.IPersistenceReader reader,
        IPersistence.IWriteBatch batch,
        Hash256? address,
        ref TreePath path,
        IReadOnlyDictionary<ValueHash256, Hash256>? storageRootRewrites)
    {
        if (path.Length > MaxPathLength)
        {
            if (_logger.IsWarn) _logger.Warn($"Reassembly recursion exceeded max depth at {path}");
            return null;
        }

        // 1) Reuse any existing trie node at this exact path.
        byte[]? existingRlp = address is null
            ? reader.TryLoadStateRlp(path, ReadFlags.None)
            : reader.TryLoadStorageRlp(address, path, ReadFlags.None);

        if (existingRlp is not null)
        {
            return ConsumeExistingNode(batch, address, ref path, existingRlp, storageRootRewrites);
        }

        // 2) Nothing at this path — probe the 16 children and recurse where any leaf descends.
        SubtreeResult[]? children = null;
        int childCount = 0;
        int lastNibble = -1;

        for (int nibble = 0; nibble < BranchChildCount; nibble++)
        {
            path.AppendMut(nibble);

            if (HasAnyLeafUnderPrefix(reader, address, in path))
            {
                SubtreeResult? child = Reassemble(reader, batch, address, ref path, storageRootRewrites);
                if (child.HasValue)
                {
                    children ??= new SubtreeResult[BranchChildCount];
                    children[nibble] = child.Value;
                    childCount++;
                    lastNibble = nibble;
                }
            }

            path.TruncateMut(path.Length - 1);
        }

        if (childCount == 0)
        {
            return null;
        }

        return childCount == 1
            ? CollapseSingleChild(batch, address, ref path, lastNibble, in children![lastNibble])
            : BuildBranch(batch, address, ref path, children!);
    }

    private SubtreeResult ConsumeExistingNode(
        IPersistence.IWriteBatch batch,
        Hash256? address,
        ref TreePath path,
        byte[] existingRlp,
        IReadOnlyDictionary<ValueHash256, Hash256>? storageRootRewrites)
    {
        TrieNode existing = new(NodeType.Unknown, existingRlp);
        existing.ResolveNode(NullTrieNodeResolver.Instance, path);

        // State leaf hitting a rewrite entry → re-encode with the new storage root.
        if (address is null
            && existing.IsLeaf
            && storageRootRewrites is not null
            && storageRootRewrites.Count > 0
            && path.Length + existing.Key!.Length == MaxPathLength)
        {
            ValueHash256 accountHash = ComputeFullPath(path, existing.Key);
            if (storageRootRewrites.TryGetValue(accountHash, out Hash256? newStorageRoot))
            {
                return RewriteStateLeaf(batch, ref path, existing, newStorageRoot!);
            }
        }

        return existing.NodeType switch
        {
            NodeType.Branch => new SubtreeResult(HashOf(existingRlp), NodeType.Branch, Key: null, InnerHash: null, Value: null),
            NodeType.Extension => new SubtreeResult(HashOf(existingRlp), NodeType.Extension, Key: existing.Key, InnerHash: existing.GetChildHash(0), Value: null),
            NodeType.Leaf => new SubtreeResult(HashOf(existingRlp), NodeType.Leaf, Key: existing.Key, InnerHash: null, Value: existing.Value.AsSpan().ToArray()),
            _ => throw new InvalidOperationException($"Unexpected node type {existing.NodeType} at {path}")
        };
    }

    /// <summary>
    /// Re-encode a leaf with a new <see cref="Account.StorageRoot"/> and persist it at the same path.
    /// </summary>
    private SubtreeResult RewriteStateLeaf(
        IPersistence.IWriteBatch batch,
        ref TreePath path,
        TrieNode existing,
        Hash256 newStorageRoot)
    {
        Account? oldAccount = _accountDecoder.Decode(existing.Value.AsSpan());
        if (oldAccount is null)
        {
            // Empty leaf value — unexpected for a state leaf; bail to caller.
            return new SubtreeResult(existing.Keccak ?? HashOf(existing.FullRlp.AsSpan().ToArray()), NodeType.Leaf, existing.Key, null, existing.Value.AsSpan().ToArray());
        }

        Account newAccount = oldAccount.WithChangedStorageRoot(newStorageRoot);
        byte[] newValueRlp = _accountDecoder.Encode(newAccount).Bytes;

        return PersistLeaf(batch, address: null, ref path, existing.Key!, newValueRlp);
    }

    private SubtreeResult CollapseSingleChild(
        IPersistence.IWriteBatch batch,
        Hash256? address,
        ref TreePath path,
        int nibble,
        in SubtreeResult child)
    {
        switch (child.Type)
        {
            case NodeType.Leaf:
                {
                    byte[] mergedKey = PrependNibble(nibble, child.Key!);
                    return PersistLeaf(batch, address, ref path, mergedKey, child.Value!);
                }
            case NodeType.Extension:
                {
                    byte[] mergedKey = PrependNibble(nibble, child.Key!);
                    return PersistExtension(batch, address, ref path, mergedKey, child.InnerHash!);
                }
            case NodeType.Branch:
                {
                    byte[] key = [(byte)nibble];
                    return PersistExtension(batch, address, ref path, key, child.Hash);
                }
            default:
                throw new InvalidOperationException($"Unexpected child type {child.Type}");
        }
    }

    private SubtreeResult BuildBranch(
        IPersistence.IWriteBatch batch,
        Hash256? address,
        ref TreePath path,
        SubtreeResult[] children)
    {
        TrieNode branch = TrieNodeFactory.CreateBranch();
        INodeData branchData = branch.NodeData!;
        for (int i = 0; i < BranchChildCount; i++)
        {
            if (children[i].Hash is not null)
            {
                branchData[i] = children[i].Hash;
            }
        }

        branch.ResolveKey(NullTrieNodeResolver.Instance, ref path);
        WriteTrieNode(batch, address, in path, branch);

        return new SubtreeResult(branch.Keccak!, NodeType.Branch, Key: null, InnerHash: null, Value: null);
    }

    private SubtreeResult PersistLeaf(
        IPersistence.IWriteBatch batch,
        Hash256? address,
        ref TreePath path,
        byte[] keyNibbles,
        byte[] valueBytes)
    {
        LeafData leafData = new LeafData { Key = keyNibbles }.CloneWithNewValue(new CappedArray<byte>(valueBytes));
        TrieNode leaf = new(leafData);

        leaf.ResolveKey(NullTrieNodeResolver.Instance, ref path);
        WriteTrieNode(batch, address, in path, leaf);

        return new SubtreeResult(leaf.Keccak!, NodeType.Leaf, Key: keyNibbles, InnerHash: null, Value: valueBytes);
    }

    private SubtreeResult PersistExtension(
        IPersistence.IWriteBatch batch,
        Hash256? address,
        ref TreePath path,
        byte[] keyNibbles,
        Hash256 childHash)
    {
        ExtensionData extData = new() { Key = keyNibbles, Value = childHash };
        TrieNode ext = new(extData);

        ext.ResolveKey(NullTrieNodeResolver.Instance, ref path);
        WriteTrieNode(batch, address, in path, ext);

        return new SubtreeResult(ext.Keccak!, NodeType.Extension, Key: keyNibbles, InnerHash: childHash, Value: null);
    }

    private static void WriteTrieNode(IPersistence.IWriteBatch batch, Hash256? address, in TreePath path, TrieNode node)
    {
        if (address is null)
            batch.SetStateTrieNode(path, node);
        else
            batch.SetStorageTrieNode(address, path, node);
    }

    private static bool HasAnyLeafUnderPrefix(IPersistence.IPersistenceReader reader, Hash256? address, in TreePath prefix)
    {
        ValueHash256 lower = prefix.ToLowerBoundPath();
        ValueHash256 upper = prefix.ToUpperBoundPath();

        if (address is null)
        {
            using IPersistence.IFlatIterator it = reader.CreateAccountIterator(lower, upper);
            return it.MoveNext();
        }
        else
        {
            using IPersistence.IFlatIterator it = reader.CreateStorageIterator(address, lower, upper);
            return it.MoveNext();
        }
    }

    private static Hash256 HashOf(byte[] rlp) => new(ValueKeccak.Compute(rlp));

    /// <summary>
    /// Build the 32-byte full path from the parent's nibble path and the leaf's internal key nibbles.
    /// Caller guarantees <paramref name="path"/>.Length + <paramref name="leafKey"/>.Length == 64.
    /// </summary>
    private static ValueHash256 ComputeFullPath(in TreePath path, byte[] leafKey)
    {
        TreePath combined = path.Append(leafKey);
        return combined.Path;
    }

    private static byte[] PrependNibble(int nibble, byte[] tail)
    {
        byte[] result = new byte[tail.Length + 1];
        result[0] = (byte)nibble;
        Array.Copy(tail, 0, result, 1, tail.Length);
        return result;
    }
}
