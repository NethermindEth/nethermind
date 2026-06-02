// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.StateComposition.Diff;

internal sealed partial class TrieDiffWalker
{
    private void DiffLeaves(TrieNode oldLeaf, TrieNode newLeaf, ref TreePath path, bool isStorage, int depth)
    {
        RecordNode(NodeType.Leaf, oldLeaf.FullRlp.Length, isStorage, added: false);
        RecordNode(NodeType.Leaf, newLeaf.FullRlp.Length, isStorage, added: true);

        if (trackDepth)
        {
            int d = Math.Min(depth, 15);
            RecordDepthLeaf(oldLeaf.FullRlp.Length, d, isStorage, added: false);
            RecordDepthLeaf(newLeaf.FullRlp.Length, d, isStorage, added: true);
        }

        if (isStorage)
        {
            return;
        }

        DecodeAndDiffAccountLeaves(oldLeaf, newLeaf, ref path);
    }

    private void DecodeAndDiffAccountLeaves(TrieNode oldLeaf, TrieNode newLeaf, ref TreePath path)
    {
        if (!TryDecodeAccount(oldLeaf, out AccountStruct oldAccount) ||
            !TryDecodeAccount(newLeaf, out AccountStruct newAccount))
        {
            return;
        }

        if (!oldAccount.HasCode && newAccount.HasCode) _contractsAdded++;
        else if (oldAccount.HasCode && !newAccount.HasCode) _contractsRemoved++;

        if (!oldAccount.HasStorage && newAccount.HasStorage) _contractsWithStorageAdded++;
        else if (oldAccount.HasStorage && !newAccount.HasStorage) _contractsWithStorageRemoved++;

        if (!oldAccount.IsTotallyEmpty && newAccount.IsTotallyEmpty) _emptyAccountsAdded++;
        else if (oldAccount.IsTotallyEmpty && !newAccount.IsTotallyEmpty) _emptyAccountsRemoved++;

        // Code-hash transition payload — captured once per account leaf, consumed by the
        // incremental tracker to refcount CodeBytesTotal. Whole-contract create/delete
        // leaves go through CollectLeaf instead and emit their own transitions there.
        bool storageUnchanged = oldAccount.StorageRoot == newAccount.StorageRoot;

        Hash256? addressHash = GetAddressHash(oldLeaf, ref path);
        if (addressHash is null) return;

        RecordCodeHashChange(addressHash.ValueHash256, oldAccount.CodeHash, newAccount.CodeHash);

        if (storageUnchanged) return;

        Hash256? normalizedOldStorage = oldAccount.HasStorage ? new Hash256(oldAccount.StorageRoot) : null;
        Hash256? normalizedNewStorage = newAccount.HasStorage ? new Hash256(newAccount.StorageRoot) : null;

        ITrieNodeResolver storageResolver = _rootResolver.GetStorageTrieNodeResolver(addressHash);
        TreePath storagePath = TreePath.Empty;

        BeginContractStorage(addressHash.ValueHash256);
        try
        {
            DiffSubtree(normalizedOldStorage, normalizedNewStorage, ref storagePath, storageResolver, isStorage: true, depth: 0);
        }
        finally
        {
            EndContractStorage();
        }
    }

    private static bool TryDecodeAccount(TrieNode leaf, out AccountStruct account)
    {
        CappedArray<byte> value = leaf.Value;
        Rlp.ValueDecoderContext ctx = new(value.AsSpan());
        return AccountDecoder.Instance.TryDecodeStruct(ref ctx, out account);
    }

    private static Hash256? GetAddressHash(TrieNode leaf, ref TreePath path)
    {
        // Null Key means a corrupted leaf — hashing the partial path would drift trackers under a wrong address.
        if (leaf.Key is null) return null;

        int prevLen = path.Length;
        path.AppendMut(leaf.Key);
        Hash256 addressHash = path.Path.ToCommitment();
        path.TruncateMut(prevLen);
        return addressHash;
    }
}
