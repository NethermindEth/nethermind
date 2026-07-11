// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Sparse;

namespace Nethermind.State.Flat;

/// <summary>
/// Reads trie nodes from the parent block's committed state for proof generation.
/// Searches: trieNodeCache → scope-local committed snapshots → outer snapshot chain → persistence.
/// Does NOT search the current block's dirty buffer (<c>_changedStateNodes</c>).
/// </summary>
public sealed class ParentStateTrieNodeReader(SnapshotBundle snapshotBundle) : ITrieNodeReader
{
    public byte[] LoadStateRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        if (snapshotBundle.TryFindCommittedStateNode(path, hash, out TrieNode? found))
        {
            if (found.Keccak is not null && found.Keccak == hash)
            {
                byte[]? rlp = found.FullRlp.IsNotNull ? found.FullRlp.ToArray() : null;
                if (rlp is not null && rlp.Length > 0)
                    return rlp;
            }
        }

        return LoadFromPersistence(path, hash, flags, address: null);
    }

    public byte[] LoadStorageRlp(Hash256 accountPathHash, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        if (snapshotBundle.TryFindCommittedStorageNode(accountPathHash, path, hash, out TrieNode? found))
        {
            if (found.Keccak is not null && found.Keccak == hash)
            {
                byte[]? rlp = found.FullRlp.IsNotNull ? found.FullRlp.ToArray() : null;
                if (rlp is not null && rlp.Length > 0)
                    return rlp;
            }
        }

        return LoadFromPersistence(path, hash, flags, accountPathHash);
    }

    private byte[] LoadFromPersistence(in TreePath path, Hash256 hash, ReadFlags flags, Hash256? address)
    {
        byte[] rlp = (address is null
            ? snapshotBundle.TryLoadStateRlp(path, hash, flags)
            : snapshotBundle.TryLoadStorageRlp(address, path, hash, flags))
            ?? throw new MissingTrieNodeException(
                $"Trie node not found in snapshots or persistence at path {path}",
                address, path, hash);

        Hash256 actual = Keccak.Compute(rlp);
        if (actual != hash)
            throw new TrieNodeHashMismatchException(path, hash, actual, address);

        return rlp;
    }
}
