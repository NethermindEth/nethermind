// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.DataMarketplace.Consumers.Shared.Domain
{
    public class GasLimits
    {
        public ulong Deposit { get; }
        public ulong Refund { get; }

        public GasLimits(ulong deposit, ulong refund)
        {
            Deposit = deposit;
            Refund = refund;
        }
    }
}
