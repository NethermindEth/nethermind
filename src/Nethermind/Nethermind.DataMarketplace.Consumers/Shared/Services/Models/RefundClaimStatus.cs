// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.DataMarketplace.Consumers.Shared.Services.Models
{
    public class RefundClaimStatus
    {
        public Keccak? TransactionHash { get; }
        public bool IsConfirmed { get; }

        private RefundClaimStatus()
        {
        }

        public RefundClaimStatus(Keccak transactionHash, bool confirmed)
        {
            TransactionHash = transactionHash;
            IsConfirmed = confirmed;
        }

        public static RefundClaimStatus Empty = new RefundClaimStatus();

        public static RefundClaimStatus Confirmed(Keccak transactionHash)
            => new RefundClaimStatus(transactionHash, true);

        public static RefundClaimStatus Unconfirmed(Keccak transactionHash)
            => new RefundClaimStatus(transactionHash, false);
    }
}
