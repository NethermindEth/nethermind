// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.Sync;

/// <summary>
/// Locally rebuilds a trie's internal nodes from the persisted ones, given the list of storage tries to rebuild.
/// Used to assemble a mixed state after snap sync when the pivot drifts.
/// </summary>
/// <remarks>
/// Node content comes from the persisted trie nodes and flat entries; untouched subtrees are reused by hash.
/// Preconditions: node must not conflict, and every leaf node in the state trie must be present.
/// </remarks>
public sealed class TrieReassembler(IPersistence persistence, ILogManager logManager) : ITrieReassembler
{
    private readonly ILogger _logger = logManager.GetClassLogger<TrieReassembler>();
    private readonly AccountDecoder _accountDecoder = AccountDecoder.Instance;

    private const int MaxPathLength = 64;
    private const int BranchChildCount = 16;

    public Hash256? TryReassemble(IReadOnlyCollection<Hash256> updatedStorageAccounts)
    {
        try
        {
            return Reassemble(updatedStorageAccounts);
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Trie reassembly failed.", e);
            return null;
        }
    }

    private Hash256? Reassemble(IReadOnlyCollection<Hash256> updatedStorageAccounts)
    {
        StorageRootRewrite[] rewrites = new StorageRootRewrite[updatedStorageAccounts.Count];
        int i = 0;
        foreach (Hash256 address in updatedStorageAccounts)
        {
            Hash256? newRoot = ReassembleStorageTrie(address);
            rewrites[i++] = new StorageRootRewrite(address.ValueHash256, newRoot ?? Keccak.EmptyTreeHash);
        }
        Array.Sort(rewrites, static (a, b) => a.AccountHash.CompareTo(b.AccountHash));

        if (_logger.IsInfo) _logger.Info($"Trie reassembly: rebuilt {rewrites.Length} storage tries; rebuilding state.");

        return ReassembleStateTrie(rewrites);
    }

    private Hash256? ReassembleStateTrie(ReadOnlySpan<StorageRootRewrite> storageRootRewrites)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);

        TreePath path = TreePath.Empty;
        TrieNode? root = ReassembleSubtree(reader, batch, address: null, ref path, storageRootRewrites);
        return root is null ? null : Persist(batch, address: null, ref path, root).Keccak;
    }

    private Hash256? ReassembleStorageTrie(Hash256 address)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);

        TreePath path = TreePath.Empty;
        TrieNode? root = ReassembleSubtree(reader, batch, address, ref path, storageRootRewrites: default);
        return root is null ? null : Persist(batch, address, ref path, root).Keccak;
    }

    private readonly record struct StorageRootRewrite(ValueHash256 AccountHash, Hash256 StorageRoot);

    private TrieNode? ReassembleSubtree(
        IPersistence.IPersistenceReader reader,
        IPersistence.IWriteBatch batch,
        Hash256? address,
        ref TreePath path,
        ReadOnlySpan<StorageRootRewrite> storageRootRewrites)
    {
        byte[]? rlp = address is null
            ? reader.TryLoadStateRlp(path, ReadFlags.None)
            : reader.TryLoadStorageRlp(address, path, ReadFlags.None);

        if (rlp is not null)
        {
            TrieNode node = new(NodeType.Unknown, rlp);
            node.ResolveNode(NullTrieNodeResolver.Instance, path);

            // No rewrites under this subtree: it is already correct, reference it by hash.
            if (storageRootRewrites.IsEmpty)
                return node;

            // A state leaf: rewrite its storage root if it has a matching rewrite, else keep it as-is.
            if (node.NodeType is NodeType.Leaf)
                return RewriteStateLeaf(batch, in path, node, storageRootRewrites);

            // branch/extension with rewrites underneath: ignore existing node and rebuild
        }

        if (path.Length == MaxPathLength)
        {
            if (address is null)
            {
                throw new InvalidOperationException($"Unexpected state leaf at {path}");
            }
            else
            {
                SlotValue value = default;
                if (reader.TryGetStorageRaw(address, path.Path, ref value))
                    return TrieNodeFactory.CreateLeaf(ReadOnlySpan<byte>.Empty, value.ToEvmBytes());
            }

            return null;
        }

        int rewriteCursor = 0;

        TrieNode?[]? children = null;
        int childCount = 0;
        int lastNibble = -1;

        for (int nibble = 0; nibble < BranchChildCount; nibble++)
        {
            int rewriteStart = rewriteCursor;
            while (rewriteCursor < storageRootRewrites.Length
                   && NibbleAt(storageRootRewrites[rewriteCursor].AccountHash, path.Length) == nibble)
                rewriteCursor++;

            path.AppendMut(nibble);

            if (HasAnyLeafUnderPrefix(reader, address, in path) || rewriteStart < rewriteCursor)
            {
                TrieNode? child = ReassembleSubtree(reader, batch, address, ref path, storageRootRewrites[rewriteStart..rewriteCursor]);
                if (child is not null)
                {
                    children ??= new TrieNode?[BranchChildCount];
                    children[nibble] = child;
                    childCount++;
                    lastNibble = nibble;
                }
            }

            path.TruncateMut(path.Length - 1);
        }

        if (childCount == 0)
            return null;

        if (childCount == 1)
        {
            TrieNode child = children![lastNibble]!;
            if (child.IsBranch)
            {
                path.AppendMut(lastNibble);
                TrieNode childRef = Persist(batch, address, ref path, child);
                path.TruncateMut(path.Length - 1);
                return TrieNodeFactory.CreateExtension([(byte)lastNibble], childRef);
            }
            else
            {
                return child.CloneWithChangedKey(HexPrefix.PrependNibble((byte)lastNibble, child.Key!));
            }
        }

        TrieNode branch = TrieNodeFactory.CreateBranch();
        for (int nibble = 0; nibble < BranchChildCount; nibble++)
        {
            if (children![nibble] is { } child)
            {
                path.AppendMut(nibble);
                branch.SetChild(nibble, Persist(batch, address, ref path, child));
                path.TruncateMut(path.Length - 1);
            }
        }

        return branch;
    }

    private TrieNode RewriteStateLeaf(
        IPersistence.IWriteBatch batch,
        in TreePath path,
        TrieNode node,
        ReadOnlySpan<StorageRootRewrite> storageRootRewrites)
    {
        Account oldAccount = _accountDecoder.Decode(node.Value.AsSpan()) ?? throw new InvalidOperationException($"Invalid account leaf at {path}");

        TreePath fullPath = path.Append(node.Key);

        Hash256? newStorageRoot = null;

        for (int i = 0; i < storageRootRewrites.Length; i++)
        {
            if (storageRootRewrites[i].AccountHash == fullPath.Path)
            {
                newStorageRoot = storageRootRewrites[i].StorageRoot;
                break;
            }
        }

        if (newStorageRoot is null)
            return node;

        Account newAccount = oldAccount.WithChangedStorageRoot(newStorageRoot);
        batch.SetAccountRaw(fullPath.Path, newAccount);

        return TrieNodeFactory.CreateLeaf(node.Key!, _accountDecoder.Encode(newAccount).Bytes);
    }

    private TrieNode Persist(IPersistence.IWriteBatch batch, Hash256? address, ref TreePath path, TrieNode node)
    {
        node.ResolveKey(NullTrieNodeResolver.Instance, ref path);
        if (node.Keccak is null) return node; //inlined node
        if (node.IsDirty) WriteTrieNode(batch, address, in path, node);
        return new TrieNode(NodeType.Unknown, node.Keccak);
    }

    private static void WriteTrieNode(IPersistence.IWriteBatch batch, Hash256? address, in TreePath path, TrieNode node)
    {
        if (address is null)
            batch.SetStateTrieNode(path, node.FullRlp.AsSpan());
        else
            batch.SetStorageTrieNode(address, path, node.FullRlp.AsSpan());
    }

    private static int NibbleAt(in ValueHash256 hash, int index)
    {
        byte b = hash.Bytes[index >> 1];
        return (index & 1) == 0 ? b >> 4 : b & 0xF;
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
}
