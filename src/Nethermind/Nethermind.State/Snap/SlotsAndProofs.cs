// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Collections;

namespace Nethermind.State.Snap
{
    /// <summary>
    /// Container for snap sync storage slot data and associated proofs.
    /// Thread-safe disposal prevents double-return of pooled buffers.
    /// The double-dispose occurs because SnapProvider.AddStorageRange disposes the response
    /// after processing, then SnapSyncFeed.HandleResponse's finally block disposes the batch
    /// (which disposes the response again). MessageDictionary.CleanOldRequests can also race
    /// from a background thread.
    /// </summary>
    public class SlotsAndProofs : IDisposable
    {
        /// <summary>Gets or sets the paths and storage slots returned by the remote peer.</summary>
        public IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> PathsAndSlots { get; set; }

        /// <summary>Gets or sets the Merkle proofs accompanying the slot data.</summary>
        public IByteArrayList Proofs { get; set; }

        private int _disposed;

        public void Dispose()
        {
            // Atomic guard: only the first caller proceeds with disposal.
            // Prevents double-return of ArrayPool buffers when SnapProvider and SnapSyncFeed
            // (or a background timeout thread) both attempt to dispose the same instance.
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            PathsAndSlots?.DisposeRecursive();
            Proofs?.Dispose();
        }
    }
}
