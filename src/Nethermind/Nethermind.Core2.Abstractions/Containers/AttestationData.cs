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
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    public class AttestationData : IEquatable<AttestationData>
    {
        public static readonly AttestationData Zero = new AttestationData(Slot.Zero, CommitteeIndex.Zero, Root.Zero,
            Checkpoint.Zero, Checkpoint.Zero);

        public AttestationData(
            Slot slot,
            CommitteeIndex index,
            Root beaconBlockRoot,
            Checkpoint source,
            Checkpoint target)
        {
            BeaconBlockRoot = beaconBlockRoot;
            Source = source;
            Target = target;
            Slot = slot;
            Index = index;
        }

        public Root BeaconBlockRoot { get; }
        public CommitteeIndex Index { get; }
        public Slot Slot { get; }
        public Checkpoint Source { get; }
        public Checkpoint Target { get; }

        public static AttestationData Clone(AttestationData other)
        {
            var clone = new AttestationData(
                other.Slot,
                other.Index,
                other.BeaconBlockRoot,
                Checkpoint.Clone(other.Source),
                Checkpoint.Clone(other.Target)
            );
            return clone;
        }

        public bool Equals(AttestationData? other)
        {
            return !(other is null)
                   && BeaconBlockRoot.Equals(other.BeaconBlockRoot)
                   && Index == other.Index
                   && Slot == other.Slot
                   && Source.Equals(other.Source)
                   && Target.Equals(other.Target);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return Equals(obj as AttestationData);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(BeaconBlockRoot, Index, Slot, Source, Target);
        }

        public override string ToString()
        {
            return $"s={Slot}_i={Index}";
        }
    }
}