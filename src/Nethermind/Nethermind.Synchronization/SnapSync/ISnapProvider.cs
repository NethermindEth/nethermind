// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;

namespace Nethermind.Synchronization.SnapSync
{
    public interface ISnapProvider
    {
        (SnapSyncBatch request, bool finished) GetNextRequest();

        bool CanSync();

        AddRangeResult AddAccountRange(AccountRange request, AccountsAndProofs response);
        AddRangeResult AddAccountRange(long blockNumber, Keccak expectedRootHash, Keccak startingHash, PathWithAccount[] accounts, byte[][] proofs = null, Keccak limitHash = null!);

        AddRangeResult AddStorageRange(StorageRange request, SlotsAndProofs response);
        AddRangeResult AddStorageRange(long blockNumber, PathWithAccount pathWithAccount, Keccak expectedRootHash, Keccak startingHash, PathWithStorageSlot[] slots, byte[][] proofs = null);

        void AddCodes(Keccak[] requestedHashes, byte[][] codes);

        void RefreshAccounts(AccountsToRefreshRequest request, byte[][] response);

        void RetryRequest(SnapSyncBatch batch);

        bool IsSnapGetRangesFinished();
        void UpdatePivot();
    }
}
