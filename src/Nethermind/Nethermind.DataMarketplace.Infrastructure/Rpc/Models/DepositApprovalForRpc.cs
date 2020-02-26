//  Copyright (c) 2018 Demerzel Solutions Limited
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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Infrastructure.Rpc.Models
{
    public class DepositApprovalForRpc
    {
        public Keccak? Id { get; set; }
        public Keccak? AssetId { get; set; }
        public string? AssetName { get; set; }
        public string? Kyc { get; set; }
        public Address? Consumer { get; set; }
        public Address? Provider { get; set; }
        public ulong? Timestamp { get; set; }
        public string? State { get; set; }

        public DepositApprovalForRpc()
        {
        }

        public DepositApprovalForRpc(DepositApproval depositApproval)
        {
            Id = depositApproval.Id;
            AssetId = depositApproval.AssetId;
            AssetName = depositApproval.AssetName;
            Kyc = depositApproval.Kyc;
            Consumer = depositApproval.Consumer;
            Provider = depositApproval.Provider;
            Timestamp = depositApproval.Timestamp;
            State = depositApproval.State.ToString().ToLowerInvariant();
        }
    }
}