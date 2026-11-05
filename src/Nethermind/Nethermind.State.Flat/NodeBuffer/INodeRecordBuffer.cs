// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State.Flat.NodeBuffer;

/// <summary>Stores encoded trie-node records and indexes trie roots by hash.</summary>
/// <remarks>
/// Pointers are buffer-local byte addresses; zero means absent. Implementations must not reuse a pointer for a different record
/// while a caller can still hold it. Records and reserved spans are borrowed: a writer holds a <see cref="PinHead"/> no newer than
/// what it writes, and a reader holds a <see cref="PinAll"/> taken before it looks up the records it reads.
/// Allocation pointers are ordered across reservations, so writes following a pin's boundary have pointers at or above it.
/// </remarks>
public interface INodeRecordBuffer : IDisposable
{
    /// <summary>Keeps the current allocation frontier and every later write readable until the pin is disposed.</summary>
    /// <exception cref="ObjectDisposedException">The buffer is disposed.</exception>
    INodeRecordBufferPin PinHead();

    /// <summary>Keeps every record borrowed while the returned pin lives valid until it is disposed.</summary>
    /// <remarks>
    /// A record may stop resolving while the pin lives, so readers fall back on a miss; records already borrowed stay valid.
    /// This lets overlapping readers not hold old records forever.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The buffer is disposed.</exception>
    IDisposable PinAll();

    /// <inheritdoc cref="INodeRecordStore.TryLocate"/>
    bool TryLocate(ulong pointer, out NodeRecord record);

    /// <inheritdoc cref="INodeRecordStore.StartWriteBatch"/>
    INodeRecordWriteBatch StartWriteBatch(int alignedTotalLength);

    /// <summary>Finds the newest available indexed pointer for a trie root, or returns zero.</summary>
    /// <param name="address">The storage trie address hash, or null for the state trie.</param>
    /// <param name="root">The root hash.</param>
    /// <param name="boundary">Roots indexed at or past this pointer are skipped, as by a reader pinned there; <see cref="ulong.MaxValue"/> for none.</param>
    ulong FindRoot(Hash256? address, in ValueHash256 root, ulong boundary);

    /// <summary>Indexes a completed node as the root of a trie.</summary>
    /// <param name="address">The storage trie address hash, or null for the state trie.</param>
    /// <param name="root">The root hash.</param>
    /// <param name="rootPointer">The pointer of the completed root record.</param>
    void SetRoot(Hash256? address, in ValueHash256 root, ulong rootPointer);
}
