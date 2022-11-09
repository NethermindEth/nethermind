//  Copyright (c) 2022 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;

namespace Nethermind.Synchronization.SnapSync
{
    public interface ISnapProvider
    {
        public event EventHandler<SnapSyncEventArgs>? StateRangesFinished;

        (SnapSyncBatch request, bool finished) GetNextRequest();

        bool CanSync();

        AddRangeResult AddAccountRange(AccountRange request, AccountsAndProofs response);
        AddRangeResult AddAccountRange(long blockNumber, Keccak expectedRootHash, Keccak startingHash, PathWithAccount[] accounts, byte[][] proofs = null);

        AddRangeResult AddStorageRange(StorageRange request, SlotsAndProofs response);
        AddRangeResult AddStorageRange(long blockNumber, PathWithAccount pathWithAccount, Keccak expectedRootHash, Keccak startingHash, PathWithStorageSlot[] slots, byte[][] proofs = null);

        void AddCodes(Keccak[] requestedHashes, byte[][] codes);

        void RefreshAccounts(AccountsToRefreshRequest request, byte[][] response);

        void RetryRequest(SnapSyncBatch batch);

        bool IsSnapGetRangesFinished();
        void UpdatePivot();
        void SetSyncStart();
    }
}
