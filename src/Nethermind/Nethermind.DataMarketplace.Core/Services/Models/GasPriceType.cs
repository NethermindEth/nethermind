// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Core.Services.Models
{
    public class GasPriceType
    {
        public string Type { get; }
        public UInt256 Price { get; }

        public GasPriceType(string type, UInt256 price)
        {
            Type = type;
            Price = price;
        }
    }
}
