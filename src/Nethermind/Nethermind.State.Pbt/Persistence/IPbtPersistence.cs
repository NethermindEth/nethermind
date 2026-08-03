// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>Durable storage for a world state's leaf blobs, trie nodes, and <see cref="StateId"/>.</summary>
public interface IPbtPersistence
{
    IReader CreateReader();

    /// <summary>Starts an atomic batch advancing the persisted state from <paramref name="from"/> to <paramref name="to"/>.</summary>
    /// <param name="toPartitionRoots">The partition roots of <paramref name="to"/>, which <see cref="StateId.StateRoot"/> does not carry.</param>
    /// <param name="flags">
    /// Applied to every write. <see cref="WriteFlags.DisableWAL"/> requires <see cref="Flush"/> for crash durability.
    /// </param>
    IWriteBatch CreateWriteBatch(in StateId from, in StateId to, in PbtPartitionRoots toPartitionRoots, WriteFlags flags);

    /// <summary>Makes preceding <see cref="WriteFlags.DisableWAL"/> writes crash-durable.</summary>
    void Flush();

    public interface IReader : IDisposable
    {
        StateId CurrentState { get; }

        /// <summary>Gets the partition roots of <see cref="CurrentState"/>.</summary>
        PbtPartitionRoots CurrentPartitionRoots { get; }

        RefCountingMemory? GetLeafBlob(in Stem stem);
        RefCountingMemory? GetTrieNode(in TrieNodeKey key);

        ValueHash256? GetFullLeaf(PbtFullKey key);
        IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateFullLeaves();
        IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateFullLeaves(PbtFullKey prefix);
    }

    public interface IWriteBatch : IDisposable
    {
        /// <summary>An empty value deletes the blob.</summary>
        void SetLeafBlob(in Stem stem, scoped ReadOnlySpan<byte> blob);

        /// <summary>An empty value deletes the node.</summary>
        void SetTrieNode(in TrieNodeKey key, scoped ReadOnlySpan<byte> node);

        void SetFullLeaf(PbtFullKey key, ValueHash256? value);
    }
}
