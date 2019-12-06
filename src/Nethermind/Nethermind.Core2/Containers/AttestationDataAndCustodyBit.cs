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

namespace Nethermind.Core2.Containers
{
    public class AttestationDataAndCustodyBit
    {
        protected bool Equals(AttestationDataAndCustodyBit other)
        {
            return Equals(Data, other.Data) && CustodyBit == other.CustodyBit;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((AttestationDataAndCustodyBit) obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((Data != null ? Data.GetHashCode() : 0) * 397) ^ CustodyBit.GetHashCode();
            }
        }

        public const int SszLength = AttestationData.SszLength + 1;
        
        public AttestationData Data { get; set; }
        
        /// <summary>
        /// Challengeable bit for the custody of shard data
        /// </summary>
        public bool CustodyBit { get; set; }
    }
}