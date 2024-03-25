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

        void AddCodes(IReadOnlyList<ValueHash256> requestedHashes, IOwnedReadOnlyList<byte[]> codes);

        void RefreshAccounts(AccountsToRefreshRequest request, IOwnedReadOnlyList<byte[]> response);

        void RetryRequest(SnapSyncBatch batch);

        bool IsSnapGetRangesFinished();
        void UpdatePivot();
    }
}
