// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Providers.Domain;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Rpc.Models
{
    public class GasLimitsForRpc
    {
        public ulong Payment { get; set; }

        public GasLimitsForRpc()
        {
        }

        public GasLimitsForRpc(GasLimits gasLimits)
        {
            Payment = gasLimits.Payment;
        }
    }
}
