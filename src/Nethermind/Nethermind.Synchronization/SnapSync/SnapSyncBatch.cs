//  Copyright (c) 2021 Demerzel Solutions Limited
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

using Nethermind.Core.Crypto;
using Nethermind.State.Snap;

namespace Nethermind.Synchronization.SnapSync
{
    public class SnapSyncBatch
    {
        public AccountRange? AccountRangeRequest { get; set; }
        public AccountsAndProofs? AccountRangeResponse { get; set; }

        public StorageRange? StorageRangeRequest { get; set; }
        public SlotsAndProofs? StorageRangeResponse { get; set; }

        public Keccak[]? CodesRequest { get; set; }
        public byte[][]? CodesResponse { get; set; }

        public AccountsToRefreshRequest? AccountsToRefreshRequest { get; set; }
        public byte[][]? AccountsToRefreshResponse { get; set; }
    }
}
