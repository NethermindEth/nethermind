// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;

namespace Nethermind.Synchronization.SnapSync
{
    public interface ISnapProvider
    {
        bool IsFinished(out SnapSyncBatch? nextBatch);

        bool CanSync();

        AddRangeResult AddAccountRange(AccountRange request, AccountsAndProofs response);

        /// <summary>Verifies and stores the returned storage range.</summary>
        /// <remarks>
        /// Replacement providers control the returned classification. To preserve built-in stale-root recovery,
        /// delegate to <see cref="SnapProvider"/>; the feed does not infer that recovery from pivot timing.
        /// </remarks>
        AddRangeResult AddStorageRange(StorageRange request, SlotsAndProofs response);

        /// <summary>Applies the storage ranges and returns the first failure, if any.</summary>
        /// <param name="madeProgress">Whether at least one account range was applied.</param>
        AddRangeResult AddStorageRange(StorageRange request, SlotsAndProofs response, out bool madeProgress)
        {
            AddRangeResult result = AddStorageRange(request, response);
            madeProgress = result == AddRangeResult.OK;
            return result;
        }

        void AddCodes(IReadOnlyList<ValueHash256> requestedHashes, IByteArrayList codes);

        AddRangeResult RefreshAccounts(AccountsToRefreshRequest request, AccountsAndProofs response);

        /// <inheritdoc cref="ProgressTracker.BeginResponse"/>
        IDisposable? BeginResponse();

        /// <summary>Hands the request back, once per batch from <see cref="IsFinished"/>.</summary>
        /// <param name="responseHandled">False requeues the work; true means the handler already did.</param>
        /// <param name="madeRangeProgress">True if the response applied verified range progress.</param>
        void ReleaseRequest(SnapSyncBatch batch, bool responseHandled, bool madeRangeProgress = false);

        bool IsSnapGetRangesFinished();

        /// <summary>
        /// Asks the state sync pivot to move in response to a streak of unusable range responses. Rate-limited, so a
        /// call is a request rather than a move; see <see cref="ProgressTracker.UpdatePivot"/>.
        /// </summary>
        void UpdatePivot();
        void Dispose();
    }
}
