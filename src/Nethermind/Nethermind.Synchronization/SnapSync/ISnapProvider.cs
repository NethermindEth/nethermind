// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        AddRangeResult AddStorageRange(StorageRange request, SlotsAndProofs response);

        void AddCodes(IReadOnlyList<ValueHash256> requestedHashes, IByteArrayList codes);

        AddRangeResult RefreshAccounts(AccountsToRefreshRequest request, AccountsAndProofs response);

        /// <summary>Hands the request back, once per batch from <see cref="IsFinished"/>.</summary>
        /// <param name="responseHandled">False requeues the work; true means the handler already did.</param>
        void ReleaseRequest(SnapSyncBatch batch, bool responseHandled);

        bool IsSnapGetRangesFinished();

        /// <summary>Asks the state sync pivot to move in response to a streak of unusable range responses.</summary>
        /// <returns>
        /// <c>true</c> if the move was requested of the pivot; <c>false</c> if it was declined, in which case the
        /// peers have not been handed a new state root and cannot be judged on the old one.
        /// </returns>
        bool UpdatePivot();
        void Dispose();
    }
}
