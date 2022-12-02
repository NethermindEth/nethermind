// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Consumers.Refunds
{
    public interface IRefundService
    {
        ulong GasLimit { get; }
        Task SetEarlyRefundTicketAsync(EarlyRefundTicket ticket, RefundReason reason);
        Task<Keccak?> ClaimRefundAsync(Address onBehalfOf, RefundClaim refundClaim, UInt256 gasPrice);
        Task<Keccak?> ClaimEarlyRefundAsync(Address onBehalfOf, EarlyRefundClaim earlyRefundClaim, UInt256 gasPrice);
    }
}
