// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Providers.Policies
{
    public interface IReceiptsPolicies
    {
        Task<bool> CanRequestReceipts(long unpaidUnits, UInt256 unitPrice);
        Task<bool> CanMergeReceipts(long unmergedUnits, UInt256 unitPrice);
        Task<bool> CanClaimPayment(long unclaimedUnits, UInt256 unitPrice);
    }
}
