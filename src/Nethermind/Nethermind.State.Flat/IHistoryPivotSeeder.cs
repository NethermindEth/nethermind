// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat;

/// <summary>
/// Hook invoked at the sync-completion seam (<see cref="Sync.FlatTreeSyncStore.FinalizeSync"/>), letting a
/// windowed flat-history implementation seed its floor at the snap-sync pivot instead of requiring an unbroken
/// capture chain back to genesis. Lives here (not in <c>Nethermind.State.Flat.History</c>, which depends on this
/// project) so the sync store can depend on the hook without a circular project reference, mirroring
/// <see cref="IFlatPersistenceCaptureHook"/>'s existing shape for the same reason.
/// </summary>
public interface IHistoryPivotSeeder
{
    /// <summary>
    /// Seeds the history floor/watermark at <paramref name="pivotBlock"/>/<paramref name="pivotStateRoot"/> from
    /// the current state of <paramref name="reader"/>. Must be called before any block processing runs on top of
    /// the pivot — the reader must reflect exactly the pivot's state, no more. A no-op when history capture or
    /// windowing is not configured.
    /// </summary>
    void SeedPivot(ulong pivotBlock, in ValueHash256 pivotStateRoot, IPersistence.IPersistenceReader reader);
}
