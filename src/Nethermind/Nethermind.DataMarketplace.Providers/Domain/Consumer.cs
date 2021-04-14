/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

[assembly: InternalsVisibleTo("Nethermind.DataMarketplace.Providers.Infrastructure")]
[assembly: InternalsVisibleTo("Nethermind.DataMarketplace.Providers.Test")]
namespace Nethermind.DataMarketplace.Providers.Domain
{
    public class Consumer : IEquatable<Consumer>
    {
        public Keccak DepositId { get; private set; }
        public uint VerificationTimestamp { get; private set; }
        public DataRequest DataRequest { get; private set; }
        public DataAsset DataAsset { get; private set; }
        public bool HasAvailableUnits { get; private set; }
        public uint ConsumedUnits { get; private set; }

        public Consumer(Keccak depositId, uint verificationTimestamp, DataRequest dataRequest,
            DataAsset dataAsset, bool hasAvailableUnits = true)
        {
            DepositId = depositId;
            VerificationTimestamp = verificationTimestamp;
            DataRequest = dataRequest;
            DataAsset = dataAsset;
            HasAvailableUnits = hasAvailableUnits;
        }

        public void SetUnavailableUnits() => HasAvailableUnits = false;

        public void SetConsumedUnits(uint units)
        {
            ConsumedUnits = units;
        }

        public bool Equals(Consumer other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Equals(DepositId, other.DepositId);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((Consumer) obj);
        }

        public override int GetHashCode()
        {
            return (DepositId != null ? DepositId.GetHashCode() : 0);
        }

        public static bool operator ==(Consumer left, Consumer right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(Consumer left, Consumer right)
        {
            return !Equals(left, right);
        }
    }
}