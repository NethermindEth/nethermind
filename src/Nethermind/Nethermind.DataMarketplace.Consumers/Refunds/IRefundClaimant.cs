// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Shared.Services.Models;

namespace Nethermind.DataMarketplace.Consumers.Refunds
{
    public interface IRefundClaimant
    {
        Task<RefundClaimStatus> TryClaimRefundAsync(DepositDetails deposit, Address refundTo);
        Task<RefundClaimStatus> TryClaimEarlyRefundAsync(DepositDetails deposit, Address refundTo);
    }
}
