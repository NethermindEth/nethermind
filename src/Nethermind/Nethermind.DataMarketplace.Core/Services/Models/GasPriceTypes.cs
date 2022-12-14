// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.DataMarketplace.Core.Services.Models
{
    public class GasPriceTypes
    {
        public GasPriceDetails SafeLow { get; }
        public GasPriceDetails Average { get; }
        public GasPriceDetails Fast { get; }
        public GasPriceDetails Fastest { get; }
        public GasPriceDetails Custom { get; }
        public string Type { get; }
        public ulong UpdatedAt { get; }

        public GasPriceTypes(GasPriceDetails safeLow, GasPriceDetails average, GasPriceDetails fast,
            GasPriceDetails fastest, GasPriceDetails custom, string type, ulong updatedAt)
        {
            SafeLow = safeLow;
            Average = average;
            Fast = fast;
            Fastest = fastest;
            Custom = custom;
            Type = type;
            UpdatedAt = updatedAt;
        }
    }
}
