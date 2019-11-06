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

using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    public class AttestationData
    {
        public bool Equals(AttestationData other)
        {
            return Slot.Equals(other.Slot) &&
                   CommitteeIndex == other.CommitteeIndex &&
                   Equals(BeaconBlockRoot, other.BeaconBlockRoot) &&
                   Source.Equals(other.Source) &&
                   Target.Equals(other.Target);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj.GetType() == GetType() && Equals((AttestationData) obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = Slot.GetHashCode();
                hashCode = (hashCode * 397) ^ CommitteeIndex.GetHashCode();
                hashCode = (hashCode * 397) ^ (BeaconBlockRoot != null ? BeaconBlockRoot.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ Source.GetHashCode();
                hashCode = (hashCode * 397) ^ Target.GetHashCode();
                return hashCode;
            }
        }

        public const int SszLength = Slot.SszLength + CommitteeIndex.SszLength + Sha256.SszLength + 2 * Checkpoint.SszLength; 
        
        public Slot Slot { get; set; }
        public CommitteeIndex CommitteeIndex { get; set; }
        public Sha256 BeaconBlockRoot { get; set; }
        public Checkpoint Source { get; set; }
        public Checkpoint Target { get; set; }

        public bool IsSlashable(AttestationData data2)
        {
            return (!ReferenceEquals(this, data2) && Target.Epoch == data2.Target.Epoch)
                   || (Source.Epoch < data2.Source.Epoch && Target.Epoch > data2.Target.Epoch);
        }
    }
}