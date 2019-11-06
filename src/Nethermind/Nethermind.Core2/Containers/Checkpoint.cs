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

using System.Diagnostics;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    [DebuggerDisplay("{Epoch}_{Root}")]
    public struct Checkpoint
    {
        public const int SszLength = Sha256.SszLength + Epoch.SszLength; 
        
        public Checkpoint(Epoch epoch, Sha256 root)
        {
            Epoch = epoch;
            Root = root;
        }

        public Epoch Epoch { get; }
        public Sha256 Root { get; }
        
        public static bool operator ==(Checkpoint a, Checkpoint b)
        {
            return a.Epoch == b.Epoch && a.Root == b.Root;
        }

        public static bool operator !=(Checkpoint a, Checkpoint b)
        {
            return !(a == b);
        }

        public bool Equals(Checkpoint other)
        {
            return Epoch == other.Epoch && Root == other.Root;
        }

        public override bool Equals(object obj)
        {
            return obj is Checkpoint other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Epoch.GetHashCode() * 397) ^ (Root != null ? Root.GetHashCode() : 0);
            }
        }

        public override string ToString()
        {
            return $"{Epoch}_{Root}";
        }
    }
}