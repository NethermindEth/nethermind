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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class FaucetRequestDetails : IEquatable<FaucetRequestDetails>
    {
        public string? Host { get; }
        public Address? Address { get; }
        public UInt256 Value { get; }
        public DateTime? Date { get; }
        public Keccak? TransactionHash { get; }

        public FaucetRequestDetails()
        {
        }

        public FaucetRequestDetails(string host, Address address, UInt256 value, DateTime date, Keccak transactionHash)
        {
            Host = host;
            Address = address;
            Value = value;
            Date = date;
            TransactionHash = transactionHash;
            Value = value;
        }

        public static FaucetRequestDetails From(EthRequest request)
            => new FaucetRequestDetails(request.Host, request.Address, request.Value, request.RequestedAt,
                request.TransactionHash);

        public static FaucetRequestDetails Empty => new FaucetRequestDetails();

        public bool Equals(FaucetRequestDetails? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return string.Equals(Host, other.Host) && Equals(Address, other.Address) && Value.Equals(other.Value) &&
                   Date.Equals(other.Date) && Equals(TransactionHash, other.TransactionHash);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((FaucetRequestDetails) obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = (Host != null ? Host.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ (Address != null ? Address.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ Value.GetHashCode();
                hashCode = (hashCode * 397) ^ Date.GetHashCode();
                hashCode = (hashCode * 397) ^ (TransactionHash != null ? TransactionHash.GetHashCode() : 0);
                return hashCode;
            }
        }
    }
}
