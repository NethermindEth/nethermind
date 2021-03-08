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

using System;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Core.Services.Models
{
    public class GasPriceDetails : IEquatable<GasPriceDetails>
    {
        public UInt256 Price { get; }
        public double WaitTime { get; }

        public GasPriceDetails(UInt256 price, double waitTime)
        {
            Price = price;
            WaitTime = waitTime;
        }

        public static GasPriceDetails Empty => new GasPriceDetails(0, 0);

        public bool Equals(GasPriceDetails? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Price.Equals(other.Price) && WaitTime.Equals(other.WaitTime);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            
            return obj.GetType() == GetType() && Equals((GasPriceDetails) obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Price.GetHashCode() * 397) ^ WaitTime.GetHashCode();
            }
        }
    }
}
