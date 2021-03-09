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

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class Session : IEquatable<Session>
    {
        protected long _consumedUnits;
        protected long _unpaidUnits;
        protected long _paidUnits;
        protected long _settledUnits;
        public Keccak Id { get; protected set; }
        public Keccak DepositId { get; protected set; }
        public Keccak DataAssetId { get; protected set; }
        public Address ConsumerAddress { get; protected set; }
        public PublicKey ConsumerNodeId { get; protected set; }
        public Address ProviderAddress { get; protected set; }
        public PublicKey ProviderNodeId { get; protected set; }
        public SessionState State { get; protected set; }
        public uint StartUnitsFromConsumer { get; protected set; }
        public uint StartUnitsFromProvider { get; protected set; }
        public ulong StartTimestamp { get; protected set; }
        public ulong FinishTimestamp { get; protected set; }

        public uint ConsumedUnits
        {
            get => (uint) _consumedUnits;
            private set => _consumedUnits = value;
        }

        public uint UnpaidUnits
        {
            get => (uint) _unpaidUnits;
            private set => _unpaidUnits = value;
        }

        public uint PaidUnits
        {
            get => (uint) _paidUnits;
            private set => _paidUnits = value;
        }

        public uint SettledUnits
        {
            get => (uint) _settledUnits;
            private set => _settledUnits = value;
        }

        public Session(Keccak id, Keccak depositId, Keccak dataAssetId, Address consumerAddress,
            PublicKey consumerNodeId, Address providerAddress, PublicKey providerNodeId, uint startUnitsFromConsumer,
            uint startUnitsFromProvider) : this(id, depositId, dataAssetId, consumerAddress, consumerNodeId,
            providerAddress, providerNodeId, SessionState.Unknown, startUnitsFromProvider, startUnitsFromConsumer)
        {
        }

        public Session(Keccak id, Keccak depositId, Keccak dataAssetId, Address consumerAddress,
            PublicKey consumerNodeId, Address providerAddress, PublicKey providerNodeId, SessionState state,
            uint startUnitsFromConsumer, uint startUnitsFromProvider, ulong startTimestamp = 0,
            ulong finishTimestamp = 0, uint consumedUnits = 0, uint unpaidUnits = 0, uint paidUnits = 0,
            uint settledUnits = 0)
        {
            Id = id;
            DepositId = depositId;
            DataAssetId = dataAssetId;
            ConsumerAddress = consumerAddress;
            ConsumerNodeId = consumerNodeId;
            ProviderAddress = providerAddress;
            ProviderNodeId = providerNodeId;
            State = state;
            StartUnitsFromProvider = startUnitsFromProvider;
            StartUnitsFromConsumer = startUnitsFromConsumer;
            StartTimestamp = startTimestamp;
            FinishTimestamp = finishTimestamp;
            _consumedUnits = consumedUnits;
            _unpaidUnits = unpaidUnits;
            _paidUnits = paidUnits;
            _settledUnits = settledUnits;
        }

        public bool Equals(Session? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Equals(Id, other.Id);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((Session) obj);
        }

        public override int GetHashCode()
        {
            return (Id != null ? Id.GetHashCode() : 0);
        }

        public static bool operator ==(Session left, Session right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(Session left, Session right)
        {
            return !Equals(left, right);
        }
    }
}
