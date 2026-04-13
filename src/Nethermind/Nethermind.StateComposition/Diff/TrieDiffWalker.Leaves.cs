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
    /// <summary>
    /// Compare two leaf nodes. Both are at the same trie path.
    /// For account trie: decode accounts to detect contract/storage changes.
    /// For storage trie: each leaf is one storage slot.
    /// </summary>
    private void DiffLeaves(TrieNode oldLeaf, TrieNode newLeaf, ref TreePath path, ITrieNodeResolver resolver, bool isStorage, int depth)
    {
        // Record both leaf nodes (old removed, new added)
        RecordNode(NodeType.Leaf, oldLeaf.FullRlp.Length, isStorage, added: false);
        RecordNode(NodeType.Leaf, newLeaf.FullRlp.Length, isStorage, added: true);

        if (_trackDepth)
        {
            int d = Math.Min(depth, 15);
            // Old leaf removed, new leaf added (same path = modification, both short+value counters net to zero)
            RecordDepthLeaf(oldLeaf.FullRlp.Length, d, isStorage, added: false);
            RecordDepthLeaf(newLeaf.FullRlp.Length, d, isStorage, added: true);
        }

        if (isStorage)
        {
            // Storage leaves: each leaf is one slot, but same path means same slot modified → net zero
            // Both exist at same path so it's an update, not add/remove
            return;
        }

        // Account trie leaves: decode to check contract and storage changes
        DecodeAndDiffAccountLeaves(oldLeaf, newLeaf, ref path);
    }

    /// <summary>
    /// Decode two account leaves and diff their contract status and storage roots.
    /// Both leaves are at the same account path (same address hash).
    /// </summary>
    private void DecodeAndDiffAccountLeaves(TrieNode oldLeaf, TrieNode newLeaf, ref TreePath path)
    {
        AccountStruct oldAccount = DecodeAccount(oldLeaf);
        AccountStruct newAccount = DecodeAccount(newLeaf);

        // Account itself: same path, same account, just modified → net zero for account count
        // (not an add or remove of an account)

        // Contract status change
        if (!oldAccount.HasCode && newAccount.HasCode) _contractsAdded++;
        else if (oldAccount.HasCode && !newAccount.HasCode) _contractsRemoved++;

        // Contract-with-storage transition (HasStorage = StorageRoot != EmptyTreeHash)
        if (!oldAccount.HasStorage && newAccount.HasStorage) _contractsWithStorageAdded++;
        else if (oldAccount.HasStorage && !newAccount.HasStorage) _contractsWithStorageRemoved++;

        // Empty-account transition (matches StateCompositionVisitor: nonce=0, balance=0, no code, no storage)
        if (!oldAccount.IsTotallyEmpty && newAccount.IsTotallyEmpty) _emptyAccountsAdded++;
        else if (oldAccount.IsTotallyEmpty && !newAccount.IsTotallyEmpty) _emptyAccountsRemoved++;

        // Storage trie diff — skip allocation when storage roots are identical
        if (oldAccount.StorageRoot == newAccount.StorageRoot) return;

        Hash256? normalizedOldStorage = oldAccount.HasStorage ? new Hash256(oldAccount.StorageRoot) : null;
        Hash256? normalizedNewStorage = newAccount.HasStorage ? new Hash256(newAccount.StorageRoot) : null;

        Hash256 addressHash = GetAddressHash(oldLeaf, ref path);
        ITrieNodeResolver storageResolver = _resolver.GetStorageTrieNodeResolver(addressHash);
        TreePath storagePath = TreePath.Empty;
        // Storage tries always start at depth 0 (independent trie)
        DiffSubtree(normalizedOldStorage, normalizedNewStorage, ref storagePath, storageResolver, isStorage: true, depth: 0);
    }

    /// <summary>
    /// Decode account leaf value to a full <see cref="AccountStruct"/>. Provides access to
    /// nonce, balance, code hash, and storage root needed for HasCode/HasStorage/IsTotallyEmpty.
    /// </summary>
    private static AccountStruct DecodeAccount(TrieNode leaf)
    {
        var value = leaf.Value;
        var ctx = new Rlp.ValueDecoderContext(value.AsSpan());
        AccountDecoder.Instance.TryDecodeStruct(ref ctx, out AccountStruct account);
        return account;
    }

    /// <summary>
    /// Get the address hash from a leaf node's position in the account trie.
    /// The full 64-nibble path at a leaf = the keccak256 hash of the address.
    /// </summary>
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
