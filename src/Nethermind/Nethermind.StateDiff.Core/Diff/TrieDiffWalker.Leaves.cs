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
        // Both leaves exist at the same path — record their RLP contributions on
        // each side. Leaf RLP includes the value payload, so a storage-slot value
        // change still produces a non-zero net delta even though the slot count
        // is unaffected.
        RecordNodeBytes(oldLeaf.FullRlp.Length, isStorage, added: false);
        RecordNodeBytes(newLeaf.FullRlp.Length, isStorage, added: true);

        if (isStorage)
        {
            // Storage leaves at the same path on both sides describe the same slot —
            // a value change does not affect the slot count, so there is nothing to
            // emit. The walker only records slot count deltas, not slot-value diffs.
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

        // Code-hash transition payload — captured once per account leaf and consumed by
        // the sidecar tracker to refcount CodeBytesTotal. Whole-contract create/delete
        // leaves go through CollectLeaf instead and emit their own transitions there.
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
        // Null Key means a corrupted leaf — hashing the partial path would drift trackers
        // under a wrong address.
        if (leaf.Key is null) return null;

        int prevLen = path.Length;
        path.AppendMut(leaf.Key);
        Hash256 addressHash = path.Path.ToCommitment();
        path.TruncateMut(prevLen);
        return addressHash;
    }
}
