// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.StateComposition.Diff;

internal sealed partial class TrieDiffWalker
{
    private void DiffLeaves(TrieNode oldLeaf, TrieNode newLeaf, ref TreePath path, bool isStorage, int depth)
    {
        // Record both leaf nodes (old removed, new added)
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
        AccountStruct oldAccount = DecodeAccount(oldLeaf);
        AccountStruct newAccount = DecodeAccount(newLeaf);

        // Contract status change
        if (!oldAccount.HasCode && newAccount.HasCode) _contractsAdded++;
        else if (oldAccount.HasCode && !newAccount.HasCode) _contractsRemoved++;

        // Contract-with-storage transition (HasStorage = StorageRoot != EmptyTreeHash)
        if (!oldAccount.HasStorage && newAccount.HasStorage) _contractsWithStorageAdded++;
        else if (oldAccount.HasStorage && !newAccount.HasStorage) _contractsWithStorageRemoved++;

        // Empty-account transition (matches StateCompositionVisitor: nonce=0, balance=0, no code, no storage)
        if (!oldAccount.IsTotallyEmpty && newAccount.IsTotallyEmpty) _emptyAccountsAdded++;
        else if (oldAccount.IsTotallyEmpty && !newAccount.IsTotallyEmpty) _emptyAccountsRemoved++;

        // Code-hash transition payload — captured once per account leaf, consumed by the
        // incremental tracker to refcount CodeBytesTotal. Whole-contract create/delete
        // leaves go through CollectLeaf instead and emit their own transitions there.
        bool storageUnchanged = oldAccount.StorageRoot == newAccount.StorageRoot;

        Hash256 addressHash = GetAddressHash(oldLeaf, ref path);
        RecordCodeHashChange(addressHash.ValueHash256, oldAccount.CodeHash, newAccount.CodeHash);

        // Storage trie diff — skip allocation when storage roots are identical
        if (storageUnchanged) return;

        Hash256? normalizedOldStorage = oldAccount.HasStorage ? new Hash256(oldAccount.StorageRoot) : null;
        Hash256? normalizedNewStorage = newAccount.HasStorage ? new Hash256(newAccount.StorageRoot) : null;

        ITrieNodeResolver storageResolver = rootResolver.GetStorageTrieNodeResolver(addressHash);
        TreePath storagePath = TreePath.Empty;

        BeginContractStorage(addressHash.ValueHash256);
        try
        {
            // Storage tries always start at depth 0 (independent trie)
            DiffSubtree(normalizedOldStorage, normalizedNewStorage, ref storagePath, storageResolver, isStorage: true, depth: 0);
        }
        finally
        {
            EndContractStorage();
        }
    }

    private static AccountStruct DecodeAccount(TrieNode leaf)
    {
        var value = leaf.Value;
        var ctx = new Rlp.ValueDecoderContext(value.AsSpan());
        AccountDecoder.Instance.TryDecodeStruct(ref ctx, out AccountStruct account);
        return account;
    }

    private static Hash256 GetAddressHash(TrieNode leaf, ref TreePath path)
    {
        // Append leaf's key nibbles to build the full 64-nibble path
        int prevLen = path.Length;
        if (leaf.Key is not null)
        {
            path.AppendMut(leaf.Key);
        }

        Hash256 addressHash = path.Path.ToCommitment();
        path.TruncateMut(prevLen);
        return addressHash;
    }
}
