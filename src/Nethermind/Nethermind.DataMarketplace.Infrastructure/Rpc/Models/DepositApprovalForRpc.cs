// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
