// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Consumers.Shared.Domain;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models
{
    public class GasLimitsForRpc
    {
        public ulong Deposit { get; }
        public ulong Refund { get; }

        public GasLimitsForRpc(GasLimits gasLimits)
        {
            Deposit = gasLimits.Deposit;
            Refund = gasLimits.Refund;
        }
    }
}
