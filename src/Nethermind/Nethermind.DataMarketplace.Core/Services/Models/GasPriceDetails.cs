// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

            return obj.GetType() == GetType() && Equals((GasPriceDetails)obj);
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
