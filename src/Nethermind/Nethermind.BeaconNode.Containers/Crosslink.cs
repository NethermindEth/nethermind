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

namespace Nethermind.BeaconNode.Containers
{
    public class Crosslink : IEquatable<Crosslink>
    {
        public Crosslink(Shard shard)
            : this(shard, Hash32.Zero, Epoch.Zero, Epoch.Zero, Hash32.Zero)
        {
        }

        public Crosslink(Shard shard,
            Hash32 parentRoot,
            Epoch startEpoch,
            Epoch endEpoch,
            Hash32 dataRoot)
        {
            Shard = shard;
            ParentRoot = parentRoot;
            StartEpoch = startEpoch;
            EndEpoch = endEpoch;
            DataRoot = dataRoot;
        }

        public Hash32 DataRoot { get; }
        public Epoch EndEpoch { get; }
        public Hash32 ParentRoot { get; }
        public Shard Shard { get; }
        public Epoch StartEpoch { get; }

        /// <summary>
        /// Creates a deep copy of the object.
        /// </summary>
        public static Crosslink Clone(Crosslink other)
        {
            var clone = new Crosslink(
                other.Shard,
                other.ParentRoot,
                other.StartEpoch,
                other.EndEpoch,
                other.DataRoot
                );
            return clone;
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as Crosslink);
        }

        public bool Equals(Crosslink? other)
        {
            return !(other is null)
                && Shard == other.Shard
                && StartEpoch == other.StartEpoch
                && EndEpoch == other.EndEpoch
                && DataRoot.Equals(other.DataRoot)
                && ParentRoot.Equals(other.ParentRoot);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(DataRoot, EndEpoch, ParentRoot, Shard, StartEpoch);
        }
    }
}
