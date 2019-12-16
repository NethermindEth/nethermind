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
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Containers
{
    public class Checkpoint : IEquatable<Checkpoint>
    {
        public Checkpoint(Epoch epoch, Hash32 root)
        {
            Epoch = epoch;
            Root = root;
        }

        public Epoch Epoch { get; }

        public Hash32 Root { get; private set; }

        public static Checkpoint Clone(Checkpoint other)
        {
            var clone = new Checkpoint(
                other.Epoch,
                other.Root);
            return clone;
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as Checkpoint);
        }

        public bool Equals(Checkpoint? other)
        {
            return !(other is null)
                && Epoch == other.Epoch
                && Root.Equals(other.Root);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Epoch, Root);
        }

        public void SetRoot(Hash32 root)
        {
            Root = root;
        }

        public override string ToString()
        {
            return $"{Epoch}:{Root.ToString().Substring(0, 12)}";
        }
    }
}
