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

using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    public class Eth1Data
    {
        public bool Equals(Eth1Data other)
        {
            return Equals(DepositRoot, other.DepositRoot) && DepositCount == other.DepositCount && Equals(BlockHash, other.BlockHash);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((Eth1Data) obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = (DepositRoot != null ? DepositRoot.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ DepositCount.GetHashCode();
                hashCode = (hashCode * 397) ^ (BlockHash != null ? BlockHash.GetHashCode() : 0);
                return hashCode;
            }
        }

        public const int SszLength = 2 * ByteLength.Hash32 + sizeof(ulong);
        
        /// <summary>
        /// Is it Keccak?
        /// </summary>
        public Hash32 DepositRoot { get; set; }
        public ulong DepositCount { get; set; }
        
        /// <summary>
        /// Is it Keccak?
        /// </summary>
        public Hash32 BlockHash { get; set; }
    }
}