// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeBuffer;

/// <summary>Stores encoded trie-node records and indexes trie roots by hash.</summary>
/// <remarks>
/// Pointers are buffer-local byte addresses; zero means absent. Implementations must not reuse a pointer for a different record
/// while a caller can still hold it. Records and reserved spans are borrowed: a writer holds a <see cref="Pin"/> no newer than
/// what it writes, and a reader holds a <see cref="PinAll"/> taken before it looks up the records it reads.
/// Allocation pointers are ordered across reservations, so writes following a pin's boundary have pointers at or above it.
/// </remarks>
public interface INodeRecordBuffer : IDisposable
{
    /// <summary>Keeps the current allocation frontier and every later write readable until the pin is disposed.</summary>
    /// <exception cref="ObjectDisposedException">The buffer is disposed.</exception>
    INodeRecordBufferPin Pin();

    /// <summary>Keeps every record that is readable now, and every later write, readable until the returned pin is disposed.</summary>
    /// <exception cref="ObjectDisposedException">The buffer is disposed.</exception>
    IDisposable PinAll();

    /// <summary>Finds a borrowed record, or misses when its pointer is absent or no longer available.</summary>
    bool TryLocate(ulong pointer, out NodeRecord record);

    /// <summary>Returns the borrowed record at a pointer.</summary>
    /// <remarks>The caller must protect the record and its spans with a pin.</remarks>
    /// <exception cref="TrieException">The pointer no longer resolves to a record.</exception>
    NodeRecord GetRecord(ulong pointer);

    /// <summary>Reserves contiguous, zero-filled bytes and returns their first pointer.</summary>
    /// <remarks>
    /// The length must be record-aligned. The returned span covers exactly the reservation; adding a relative byte offset
    /// to the returned pointer addresses the same byte in that span. The reservation cannot cross a pointer-address boundary.
    /// The writer must keep the reservation protected until all written records and their roots are published.
    /// </remarks>
    ulong Reserve(int alignedTotalLength, out Span<byte> bytes);

    /// <summary>Registers fully encoded records before their roots are published.</summary>
    /// <param name="basePointer">The pointer returned by the reservation.</param>
    /// <param name="bytes">The reserved bytes containing the completed records.</param>
    /// <param name="recordOffsets">Offsets of ordinary node records relative to the reservation, excluding root-index records.</param>
    void CompleteWrite(ulong basePointer, ReadOnlySpan<byte> bytes, ReadOnlySpan<int> recordOffsets);

    /// <summary>Finds the newest available indexed pointer for a trie root, or returns zero.</summary>
    /// <param name="address">The storage trie address hash, or null for the state trie.</param>
    /// <param name="root">The root hash.</param>
    ulong FindRoot(Hash256? address, in ValueHash256 root);

    /// <summary>Indexes a completed node as the root of a trie.</summary>
    /// <param name="address">The storage trie address hash, or null for the state trie.</param>
    /// <param name="root">The root hash.</param>
    /// <param name="rootPointer">The pointer of the completed root record.</param>
    void SetRoot(Hash256? address, in ValueHash256 root, ulong rootPointer);
}
