// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Pbt.NodeBuffer;

/// <summary>Stores PBT node-group records with descendant pointers and indexes tree roots by hash.</summary>
/// <remarks>
/// Pointers are buffer-local <c>(generation &lt;&lt; 32) | byteOffset</c> addresses; zero means absent. Records and reserved spans are
/// borrowed: a writer holds a <see cref="PinHead"/> no newer than what it writes, and a reader holds a <see cref="PinAll"/> taken before it
/// looks up the records it reads. Allocation pointers are ordered across reservations, so writes following a pin's boundary have pointers
/// at or above it.
/// </remarks>
public interface IPbtGroupRecordBuffer
{
    /// <summary>Keeps the current allocation frontier and every later record readable until the pin is disposed.</summary>
    /// <exception cref="ObjectDisposedException">The buffer is disposed.</exception>
    IPbtGroupRecordBufferPin PinHead();

    /// <summary>Keeps every record borrowed while the returned pin lives valid until it is disposed.</summary>
    /// <remarks>
    /// A record may stop resolving while the pin lives, so readers fall back on a miss; records already borrowed stay valid.
    /// This lets overlapping readers not hold old records forever.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The buffer is disposed.</exception>
    IDisposable PinAll();

    /// <summary>Resolves a pointer without searching snapshot layers.</summary>
    /// <remarks>A miss is resolved through the caller's persisted view. The caller's pin must outlive all uses of the returned view.</remarks>
    bool TryLocate(ulong pointer, out PbtGroupRecord record);

    /// <summary>Reserves contiguous buffer space for a group record and exposes its borrowed writable payload.</summary>
    /// <returns>The record's pointer.</returns>
    /// <remarks>
    /// Only the header is initialized. The caller must fill <paramref name="payload"/> before handing the pointer to a parent,
    /// journal, or owner, and hold a <see cref="PinHead"/> taken before the reservation while using the span.
    /// </remarks>
    ulong Reserve(PbtStorageNodePath path, int payloadLength, in PbtGroupLinks links, out Span<byte> payload);

    /// <summary>Finds the pointer of the root group of <paramref name="root"/>, or returns zero when it is not indexed or its record is no longer resident.</summary>
    ulong FindRoot(in ValueHash256 root);

    /// <summary>Indexes a published root group as the root of a tree.</summary>
    /// <param name="root">The root hash.</param>
    /// <param name="rootPointer">The pointer of the published root group record.</param>
    /// <exception cref="ArgumentException"><paramref name="rootPointer"/> is zero.</exception>
    void SetRoot(in ValueHash256 root, ulong rootPointer);
}
