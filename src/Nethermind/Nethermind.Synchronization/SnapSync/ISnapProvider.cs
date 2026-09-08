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
        void UpdatePivot();
        void Dispose();
    }
}
