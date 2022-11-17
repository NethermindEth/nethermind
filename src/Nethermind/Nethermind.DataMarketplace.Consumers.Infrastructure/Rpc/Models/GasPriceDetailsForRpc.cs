// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models
{
    public class GasPriceDetailsForRpc
    {
        public UInt256 Price { get; }
        public double WaitTime { get; }

        public GasPriceDetailsForRpc(GasPriceDetails details)
        {
            Price = details.Price;
            WaitTime = details.WaitTime;
        }
    }
}
