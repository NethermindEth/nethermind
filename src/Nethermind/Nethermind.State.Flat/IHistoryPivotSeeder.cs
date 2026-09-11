// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat;

/// <summary>Hook invoked at the sync-completion seam, letting a windowed flat-history implementation seed its
/// floor at the snap-sync pivot. Lives here (not in the history project, which depends on this one) to avoid a
/// circular project reference, mirroring <see cref="IFlatPersistenceCaptureHook"/>.</summary>
public interface IHistoryPivotSeeder
{
    /// <summary>
    /// Seeds the history floor/watermark at <paramref name="pivotBlock"/>/<paramref name="pivotStateRoot"/>. Must
    /// be called before any block processing runs on top of the pivot. A no-op when history capture or windowing
    /// is not configured.
    /// </summary>
    void SeedPivot(ulong pivotBlock, in ValueHash256 pivotStateRoot);
}
