// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State.Snap;

namespace Nethermind.Synchronization.SnapSync;

public interface ISnapTrieFactory
{
    // Called once at the start/end of a snap-sync run from SnapSyncRunner.Run — sequential, no concurrent invocations.
    void EnsureInitialize() { }
    void FinalizeSync() { }

    ISnapTree<PathWithAccount> CreateStateTree();
    ISnapTree<PathWithStorageSlot> CreateStorageTree(in ValueHash256 accountPath);

    // Marked when the range phase drains, read after EnsureInitialize, so a later run over the same data
    // skips the phase. Records nothing by default, so a backend that does not keep its store across runs
    // re-requests the ranges instead of skipping them over data it discarded.
    bool IsRangePhaseFinished() => false;
    void MarkRangePhaseFinished() { }

    /// <summary>Whether this backend saves checkpoints, so that a restarted range phase can resume.</summary>
    bool SupportsCheckpoints => false;

    /// <summary>Returns the checkpoint the range phase resumes from, or <c>null</c> to start it over.</summary>
    /// <remarks>
    /// Read after <see cref="EnsureInitialize"/>, which keeps the data a saved checkpoint covers and discards what
    /// was written after it. A backend that does not support resuming saves nothing and returns <c>null</c>.
    /// </remarks>
    SnapCheckpoint? LoadCheckpoint() => null;

    /// <summary>Makes everything written so far durable, then durably saves <paramref name="checkpoint"/>, or deletes the saved one when it is <c>null</c>.</summary>
    void SaveCheckpoint(SnapCheckpoint? checkpoint) { }
}
