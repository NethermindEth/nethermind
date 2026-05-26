// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.Sparse;

namespace Nethermind.State.Flat;

/// <summary>
/// <see cref="ITrieNodeReader"/> backed by <see cref="IPersistence.IPersistenceReader"/> (flat DB path-based storage).
/// Loads by path and validates the loaded RLP hash against the expected hash.
/// <remarks>
/// Non-owning: the caller manages the <see cref="IPersistence.IPersistenceReader"/> lifetime.
/// This adapter does not implement <see cref="System.IDisposable"/>.
/// </remarks>
/// </summary>
public sealed class FlatTrieNodeReader(IPersistence.IPersistenceReader persistenceReader) : ITrieNodeReader
{
    public byte[] LoadStateRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        byte[] rlp = persistenceReader.TryLoadStateRlp(path, flags)
            ?? throw new MissingTrieNodeException("Flat DB state trie node not found", null, path, hash);
        ValidateHash(rlp, hash, path, null);
        return rlp;
    }

    public byte[] LoadStorageRlp(Hash256 accountPathHash, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
    {
        byte[] rlp = persistenceReader.TryLoadStorageRlp(accountPathHash, path, flags)
            ?? throw new MissingTrieNodeException("Flat DB storage trie node not found", accountPathHash, path, hash);
        ValidateHash(rlp, hash, path, accountPathHash);
        return rlp;
    }

    private static void ValidateHash(byte[] rlp, Hash256 expectedHash, in TreePath path, Hash256? address)
    {
        Hash256 actual = Keccak.Compute(rlp);
        if (actual != expectedHash)
            throw new TrieNodeHashMismatchException(path, expectedHash, actual, address);
    }
}
