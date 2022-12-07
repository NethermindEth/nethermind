// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Services.Models;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models
{
    public class GasPriceTypesForRpc
    {
        public GasPriceDetailsForRpc? SafeLow { get; }
        public GasPriceDetailsForRpc? Average { get; }
        public GasPriceDetailsForRpc? Fast { get; }
        public GasPriceDetailsForRpc? Fastest { get; }
        public GasPriceDetailsForRpc? Custom { get; }
        public string? Type { get; }
        public ulong UpdatedAt { get; }

        public GasPriceTypesForRpc(GasPriceTypes types)
        {
            SafeLow = new GasPriceDetailsForRpc(types.SafeLow);
            Average = new GasPriceDetailsForRpc(types.Average);
            Fast = new GasPriceDetailsForRpc(types.Fast);
            Fastest = new GasPriceDetailsForRpc(types.Fastest);
            Custom = new GasPriceDetailsForRpc(types.Custom);
            Type = types.Type;
            UpdatedAt = types.UpdatedAt;
        }
    }
}
