// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Stores trie-node RLP bytes back-to-back in its own files, separate from the
/// metadata HSST arena files held by <see cref="IArenaManager"/>. A
/// <see cref="NodeRef"/> embedded in a persisted snapshot's metadata points at
/// <c>(BlobArenaId, byte offset)</c>; the manager resolves the id to the
/// reservation that contains the byte.
///
/// <para>
/// Wiring convention: each persisted-snapshot pool tier is a pair —
/// <c>(ArenaManager metadata, BlobArenaManager blobs)</c>. There are two such pairs,
/// Small (short-range, <c>To-From &lt; CompactSize</c>) and Large (everything else),
/// instantiated side-by-side in <c>FlatWorldStateModule</c>. BlobArenaManager itself
/// is not pool-aware — a caller picks which instance to talk to.
/// </para>
///
/// <para>
/// Refcounting: each blob arena reservation has the usual
/// <see cref="ArenaReservation"/> lease. Snapshots <see cref="AcquireBlobArena"/> on
/// construction and <see cref="ReleaseBlobArena"/> on cleanup. When the last lease
/// drops, the reservation's <c>CleanUp</c> calls <see cref="ArenaManager.MarkDead"/>,
/// which deletes the underlying file once every reservation in it is dead.
/// </para>
///
/// <para>
/// Pass 1 of the BlobArena refactor introduces this type as scaffolding. The
/// builder, catalog, and read paths continue to use the inline-RLP layout owned by
/// <see cref="IArenaManager"/> until pass 2 wires the writer through.
/// </para>
/// </summary>
public interface IBlobArenaManager : IDisposable
{
    /// <summary>
    /// Rehydrate the in-memory reservation map from the blob arena catalog
    /// (entries for this manager's pool only). Must run before any
    /// <c>PersistedSnapshot</c> is constructed.
    /// </summary>
    void Initialize(IReadOnlyList<BlobArenaCatalog.Entry> allEntries);

    /// <summary>
    /// Open a writer that appends RLP items to a freshly-allocated reservation.
    /// The returned writer exposes <see cref="BlobArenaWriter.WriteRlp"/>, which
    /// returns the <see cref="NodeRef"/> to embed in the metadata HSST for the
    /// just-written item.
    /// </summary>
    BlobArenaWriter CreateWriter(long estimatedSize, string tag);

    /// <summary>
    /// Random-access read into the reservation backing <paramref name="blobArenaId"/>.
    /// Used by the <c>NodeRef</c> dereference path on the read side.
    /// </summary>
    int RandomRead(ushort blobArenaId, long offset, Span<byte> destination);

    /// <summary>
    /// Increment the refcount on the reservation backing <paramref name="blobArenaId"/>
    /// and hand back a <see cref="BlobArenaFile"/> wrapping it. Returns false if
    /// this manager doesn't know the id. Disposing the returned
    /// <see cref="BlobArenaFile"/> calls back into <see cref="ReleaseBlobArena"/>.
    /// </summary>
    bool TryLeaseFile(ushort blobArenaId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BlobArenaFile? file);

    /// <summary>
    /// Decrement the refcount. When the last referencing snapshot is released the
    /// reservation's <c>CleanUp</c> runs <see cref="ArenaManager.MarkDead"/>, which
    /// deletes the underlying file once every reservation in it is dead. Typically
    /// invoked indirectly via <see cref="BlobArenaFile.Dispose"/>.
    /// </summary>
    void ReleaseBlobArena(ushort blobArenaId);

    /// <summary>Number of blob arena files currently open. Telemetry only.</summary>
    int BlobArenaFileCount { get; }

    /// <summary>Total mmap'd bytes across blob arena files. Telemetry only.</summary>
    long BlobArenaMappedBytes { get; }
}
