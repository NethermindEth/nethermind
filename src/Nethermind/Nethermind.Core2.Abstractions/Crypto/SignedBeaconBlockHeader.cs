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
using Nethermind.Core2.Containers;

namespace Nethermind.Core2.Crypto
{
    public class SignedBeaconBlockHeader : IEquatable<SignedBeaconBlockHeader>
    {
        public static readonly SignedBeaconBlockHeader Zero =
            new SignedBeaconBlockHeader(BeaconBlockHeader.Zero, BlsSignature.Zero);
        
        public SignedBeaconBlockHeader(BeaconBlockHeader message, BlsSignature signature)
        {
            Message = message;
            Signature = signature;
        }

        public BeaconBlockHeader Message { get; }
        public BlsSignature Signature { get; }

        public bool Equals(SignedBeaconBlockHeader? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Message.Equals(other.Message) && Signature.Equals(other.Signature);
        }

        public override bool Equals(object? obj)
        {
            return ReferenceEquals(this, obj) || obj is SignedBeaconBlockHeader other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Message, Signature);
        }
    }
}