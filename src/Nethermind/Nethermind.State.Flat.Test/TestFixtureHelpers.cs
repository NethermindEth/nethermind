// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Storage;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Helpers shared across the test fixtures that wrap synthesised
/// <see cref="PersistedSnapshot"/> instances.
/// </summary>
internal static class TestFixtureHelpers
{
    /// <summary>
    /// Read the <c>ref_ids</c> list from the metadata HSST inside <paramref name="reservation"/>
    /// and acquire a lease per id on <paramref name="blobs"/>. Mirrors what
    /// <c>PersistedSnapshotRepository</c> does at load time — the resulting
    /// <see cref="PersistedSnapshot"/>'s <c>CleanUp</c> drops one lease per id, keeping
    /// refcounts balanced. No-op when the HSST has no ref_ids (raw test bytes that aren't
    /// a real HSST).
    /// </summary>
    public static void LeaseBlobIdsFromHsst(ArenaReservation reservation, BlobArenaManager blobs)
    {
        using WholeReadSession session = reservation.BeginWholeReadSession();
        WholeReadSessionReader reader = session.GetReader();
        ushort[]? ids = PersistedSnapshot.ReadRefIdsFromMetadata<WholeReadSessionReader, NoOpPin>(in reader);
        if (ids is null) return;
        foreach (ushort id in ids)
        {
            if (!blobs.TryLeaseFile(id, out _))
                throw new System.InvalidOperationException(
                    $"Test fixture's BlobArenaManager has no slot for id {id}; did Build() use a different manager?");
        }
    }
}
