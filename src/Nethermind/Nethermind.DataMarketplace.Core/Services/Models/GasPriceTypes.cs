//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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