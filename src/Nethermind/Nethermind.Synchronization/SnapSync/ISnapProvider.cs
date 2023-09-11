// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State.Snap;

namespace Nethermind.Synchronization.SnapSync
{
    public interface ISnapProvider
    {
        (SnapSyncBatch request, bool finished) GetNextRequest();

        bool CanSync();

        AddRangeResult AddAccountRange(AccountRange request, AccountsAndProofs response);

        AddRangeResult AddStorageRange(StorageRange request, SlotsAndProofs response);

        void AddCodes(ValueKeccak[] requestedHashes, byte[][] codes);

        void RefreshAccounts(AccountsToRefreshRequest request, byte[][] response);

        void RetryRequest(SnapSyncBatch batch);

        bool IsSnapGetRangesFinished();
        void UpdatePivot();
    }
}
