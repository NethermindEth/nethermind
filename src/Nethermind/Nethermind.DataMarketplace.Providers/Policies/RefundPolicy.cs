// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.DataMarketplace.Providers.Policies
{
    public class RefundPolicy : IRefundPolicy
    {
        public uint GetClaimableAfterUnits(Keccak depositId)
        {
            // 1 day
            return 60 * 60 * 24;
        }
    }
}
