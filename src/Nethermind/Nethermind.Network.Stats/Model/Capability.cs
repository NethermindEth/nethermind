//  Copyright (c) 2021 Demerzel Solutions Limited
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

namespace Nethermind.Stats.Model
{
    public class Capability : IEquatable<Capability>
    {
        public Capability(string protocolCode, int version)
        {
            ProtocolCode = protocolCode;
            Version = version;
        }

        public string ProtocolCode { get; }
        public int Version { get; }

        public bool Equals(Capability other)
        {
            if (ReferenceEquals(null, other))
            {
                return false;
            }

            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return string.Equals(ProtocolCode, other.ProtocolCode) && Version == other.Version;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }

            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            return obj.GetType() == GetType() && Equals((Capability)obj);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(ProtocolCode, Version);
        }

        public override string ToString()
        {
            return string.Concat(ProtocolCode, Version);
        }
    }
}
