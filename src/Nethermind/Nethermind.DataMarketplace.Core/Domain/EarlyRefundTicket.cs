// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class EarlyRefundTicket
    {
        public Keccak DepositId { get; }
        public uint ClaimableAfter { get; }
        public Signature Signature { get; }

        public EarlyRefundTicket(Keccak depositId, uint claimableAfter, Signature signature)
        {
            DepositId = depositId;
            ClaimableAfter = claimableAfter;
            Signature = signature;
        }
    }
}
