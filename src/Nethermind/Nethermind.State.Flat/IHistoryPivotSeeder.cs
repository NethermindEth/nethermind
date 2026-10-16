// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat;

/// <summary>Hook invoked at the sync-completion seam, letting a flat-history implementation seed its floor at the
/// snap-sync pivot. Lives here (not in the history project, which depends on this one) to avoid a circular project
/// reference, mirroring <see cref="IFlatPersistenceCaptureHook"/>.</summary>
public interface IHistoryPivotSeeder
{
    /// <summary>
    /// Seeds the history floor/watermark at <paramref name="pivotBlock"/>/<paramref name="pivotStateRoot"/>. Must
    /// be called before any block processing runs on top of the pivot. A no-op when history capture is not configured.
    /// </summary>
    /// <param name="accounts">Every account of the pivot state with its full hashed path, which an unwindowed history
    /// needs as explicit rows; the flat account column keys a truncated path and cannot provide it.</param>
    void SeedPivot(ulong pivotBlock, in ValueHash256 pivotStateRoot, IPivotAccountSource? accounts = null);
}

/// <summary>Enumerates the accounts of the pivot state.</summary>
public interface IPivotAccountSource
{
    /// <summary>Calls <paramref name="sink"/> once per account; calls may come from several threads at once.</summary>
    void ForEachAccount(Action<ValueHash256, Account> sink, CancellationToken cancellationToken);
}
