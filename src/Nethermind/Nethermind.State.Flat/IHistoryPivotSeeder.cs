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
    /// <remarks>Constant time: an unwindowed history only records that the pivot's state is to be written as history
    /// rows. The rows are written in the background, and the flat persist is held back until they are, see
    /// <see cref="IFlatPersistenceCaptureHook.HoldsFlatPersist"/>.</remarks>
    void SeedPivot(ulong pivotBlock, in ValueHash256 pivotStateRoot);
}

/// <summary>Reads the accounts of the persisted flat state.</summary>
public interface IPivotStateSource
{
    /// <summary>The state the flat columns hold.</summary>
    StateId PersistedState { get; }

    /// <summary>Calls <paramref name="sink"/> once per account of the state at <paramref name="stateRoot"/> whose
    /// hashed address starts with the byte <paramref name="partition"/>, on the calling thread.</summary>
    void ForEachAccount(in ValueHash256 stateRoot, byte partition, Action<ValueHash256, Account> sink, CancellationToken cancellationToken);
}
