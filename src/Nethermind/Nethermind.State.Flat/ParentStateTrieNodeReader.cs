// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Sparse;

namespace Nethermind.State.Flat;

/// <summary>
/// Reads trie nodes from the parent block's committed state for proof generation.
/// Uses <see cref="ReadOnlySnapshotBundle"/> (snapshot chain + persistence reader).
/// Does NOT search the current block's dirty buffers — proofs reference the PREVIOUS state.
/// Thread-safe: <see cref="ReadOnlySnapshotBundle"/> is immutable.
/// </summary>
public sealed class ParentStateTrieNodeReader(ReadOnlySnapshotBundle roBundle) : ITrieNodeReader
{
    public byte[] LoadStateRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        // Step 1: Search committed snapshot chain by path
        if (roBundle.TryFindStateNodes(path, hash, out TrieNode? found))
        {
            // Validate hash — snapshot matches by path, hash may differ if overwritten
            if (found.Keccak is not null && found.Keccak != hash)
                return LoadFromPersistence(path, hash, flags, address: null);

            byte[]? rlp = found.FullRlp.IsNotNull ? found.FullRlp.ToArray() : null;
            if (rlp is not null && rlp.Length > 0)
                return rlp;
        }

        // Step 2: Fall back to persistence reader (flat DB columns)
        return LoadFromPersistence(path, hash, flags, address: null);
    }

    public byte[] LoadStorageRlp(Hash256 accountPathHash, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        if (roBundle.TryFindStorageNodes(accountPathHash, path, hash, out TrieNode? found))
        {
            if (found.Keccak is not null && found.Keccak != hash)
                return LoadFromPersistence(path, hash, flags, accountPathHash);

            byte[]? rlp = found.FullRlp.IsNotNull ? found.FullRlp.ToArray() : null;
            if (rlp is not null && rlp.Length > 0)
                return rlp;
        }

        return LoadFromPersistence(path, hash, flags, accountPathHash);
    }

    private byte[] LoadFromPersistence(in TreePath path, Hash256 hash, ReadFlags flags, Hash256? address)
    {
        byte[] rlp = (address is null
            ? roBundle.TryLoadStateRlp(path, hash, flags)
            : roBundle.TryLoadStorageRlp(address, path, hash, flags))
            ?? throw new MissingTrieNodeException(
                $"Trie node not found in snapshots or persistence at path {path}",
                address, path, hash);

        Hash256 actual = Keccak.Compute(rlp);
        if (actual != hash)
            throw new TrieNodeHashMismatchException(path, hash, actual, address);

        return rlp;
    }
}
