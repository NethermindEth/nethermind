// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.StateDiff.Core.Diff;

public sealed partial class TrieDiffWalker
{
    private void DiffLeaves(TrieNode oldLeaf, TrieNode newLeaf, ref TreePath path, ResolverPair resolvers, bool isStorage)
    {
        // Leaf RLP carries the value, so a value change nets a non-zero byte delta even when slot count is unchanged.
        RecordNodeBytes(oldLeaf.FullRlp.Length, isStorage, added: false);
        RecordNodeBytes(newLeaf.FullRlp.Length, isStorage, added: true);

        if (isStorage)
        {
            // Same storage slot on both sides; the walker records slot-count deltas, not slot-value diffs.
            return;
        }

        DecodeAndDiffAccountLeaves(oldLeaf, newLeaf, ref path, resolvers);
    }

    private void DecodeAndDiffAccountLeaves(TrieNode oldLeaf, TrieNode newLeaf, ref TreePath path, ResolverPair resolvers)
    {
        if (!TryDecodeAccount(oldLeaf, out AccountStruct oldAccount) ||
            !TryDecodeAccount(newLeaf, out AccountStruct newAccount))
        {
            return;
        }

        Hash256? addressHash = GetAddressHash(oldLeaf, ref path);
        if (addressHash is null) return;

        RecordCodeHashChange(addressHash.ValueHash256, oldAccount.CodeHash, newAccount.CodeHash);

        if (oldAccount.StorageRoot == newAccount.StorageRoot) return;

        Hash256? normalizedOldStorage = oldAccount.HasStorage ? new Hash256(oldAccount.StorageRoot) : null;
        Hash256? normalizedNewStorage = newAccount.HasStorage ? new Hash256(newAccount.StorageRoot) : null;

        ResolverPair storageResolvers = resolvers.ForStorage(addressHash);
        TreePath storagePath = TreePath.Empty;

        BeginContractStorage(addressHash.ValueHash256);
        try
        {
            DiffSubtree(normalizedOldStorage, normalizedNewStorage, ref storagePath, storageResolvers, isStorage: true);
        }
        finally
        {
            EndContractStorage();
        }
    }

    private static bool TryDecodeAccount(TrieNode leaf, out AccountStruct account)
    {
        CappedArray<byte> value = leaf.Value;
        RlpReader ctx = new(value.AsSpan());
        return AccountDecoder.Instance.TryDecodeStruct(ref ctx, out account);
    }

    private static Hash256? GetAddressHash(TrieNode leaf, ref TreePath path)
    {
        // Null Key = corrupted leaf; hashing the partial path would attribute to the wrong address.
        if (leaf.Key is null) return null;

        int prevLen = path.Length;
        path.AppendMut(leaf.Key);
        Hash256 addressHash = path.Path.ToCommitment();
        path.TruncateMut(prevLen);
        return addressHash;
    }
}
