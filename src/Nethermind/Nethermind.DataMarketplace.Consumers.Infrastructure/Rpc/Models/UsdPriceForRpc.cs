// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models
{
    public class UsdPriceForRpc
    {
        public decimal Price { get; }
        public ulong UpdatedAt { get; }

        public UsdPriceForRpc(decimal price, ulong updatedAt)
        {
            Price = price;
            UpdatedAt = updatedAt;
        }
    }
}
