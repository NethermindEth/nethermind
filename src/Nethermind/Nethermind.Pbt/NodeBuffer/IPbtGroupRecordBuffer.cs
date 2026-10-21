// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Pbt.NodeBuffer;

/// <summary>Stores PBT node-group records with direct descendant references and indexes tree roots by hash.</summary>
/// <remarks>
/// References are local to a buffer. Records and reserved spans are borrowed: a writer holds a <see cref="PinHead"/> no newer than
/// what it writes, and a reader holds a <see cref="PinAll"/> taken before it looks up the records it reads. References order by
/// allocation, so records reserved after a pin compare greater than or equal to its write boundary.
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

    /// <summary>Resolves a direct reference without searching snapshot layers.</summary>
    /// <remarks>A miss is resolved through the caller's persisted view. The caller's pin must outlive all uses of the returned view.</remarks>
    bool TryLocate(PbtGroupReference reference, out PbtGroupRecord record);

    /// <summary>Reserves contiguous buffer space for a group record and exposes its borrowed writable payload.</summary>
    /// <returns>The record's reference.</returns>
    /// <remarks>
    /// Only the header is initialized. The caller must fill <paramref name="payload"/> before handing the reference to a parent,
    /// journal, or owner, and hold a <see cref="PinHead"/> taken before the reservation while using the span.
    /// </remarks>
    PbtGroupReference Reserve(PbtStorageNodePath path, int payloadLength, in PbtGroupLinks links, out Span<byte> payload);

    /// <summary>The root group of <paramref name="root"/>, or Unknown when it is not indexed or its record is no longer resident.</summary>
    PbtGroupReference FindRoot(in ValueHash256 root);

    /// <summary>Indexes <paramref name="reference"/> as the root group of <paramref name="root"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="reference"/> is not a direct reference.</exception>
    void SetRoot(in ValueHash256 root, PbtGroupReference reference);

    /// <summary>Maps a reference of <paramref name="source"/>, such as the root of a snapshot written there, into this buffer.</summary>
    /// <returns>The reference this buffer resolves the same record by, or Unknown when this buffer cannot reach it.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="source"/> is unrelated to this buffer.</exception>
    PbtGroupReference MapReference(IPbtGroupRecordBuffer source, PbtGroupReference reference);
}
