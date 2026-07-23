// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>
/// Durable storage of one world state: zone-partitioned stem leaf blobs, stem trie nodes, and the
/// <see cref="StateId"/> they collectively represent.
/// </summary>
public interface IPbtPersistence
{
    IReader CreateReader();

    /// <summary>
    /// Starts an atomic batch advancing the persisted state <paramref name="from"/> →
    /// <paramref name="to"/>; throws when the currently persisted state is not <paramref name="from"/>.
    /// </summary>
    /// <param name="toTreeRoot">The EIP-8297 root of <paramref name="to"/>, which its header-derived <see cref="StateId.StateRoot"/> does not carry.</param>
    /// <param name="flags">
    /// Applied to every write of the batch. <see cref="WriteFlags.DisableWAL"/> makes the batch
    /// non-durable until <see cref="Flush"/> is called, so it is only safe for bulk writes that are
    /// restarted from scratch on a crash.
    /// </param>
    IWriteBatch CreateWriteBatch(in StateId from, in StateId to, in ValueHash256 toTreeRoot, WriteFlags flags);

    /// <summary>Materializes every column's memtable, making prior <see cref="WriteFlags.DisableWAL"/> writes crash-durable.</summary>
    void Flush();

    public interface IReader : IDisposable
    {
        StateId CurrentState { get; }

        /// <inheritdoc cref="CreateWriteBatch" path="/param[@name='toTreeRoot']"/>
        ValueHash256 CurrentTreeRoot { get; }

        RefCountingMemory? GetLeafBlob(in Stem stem);
        RefCountingMemory? GetTrieNode(in TrieNodeKey key);
    }

    public interface IWriteBatch : IDisposable
    {
        /// <summary>Null or empty deletes the blob.</summary>
        void SetLeafBlob(in Stem stem, byte[]? blob);

        /// <summary>Null deletes the node.</summary>
        void SetTrieNode(in TrieNodeKey key, byte[]? node);
    }
}
