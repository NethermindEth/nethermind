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

namespace Nethermind.Core2.Types
{
    public struct Slot
    {
        public const int SszLength = sizeof(ulong);
        
        public static Slot Genesis = default;
        
        public Slot(ulong number)
        {
            Number = number;
        }
        
        public ulong Number { get; }

        public static bool operator <(Slot a, Slot b)
        {
            return a.Number < b.Number;
        }

        public static bool operator >(Slot a, Slot b)
        {
            return a.Number > b.Number;
        }
        
        public static bool operator <=(Slot a, Slot b)
        {
            return a.Number <= b.Number;
        }

        public static bool operator >=(Slot a, Slot b)
        {
            return a.Number >= b.Number;
        }
        
        public static bool operator ==(Slot a, Slot b)
        {
            return a.Number == b.Number;
        }

        public static bool operator !=(Slot a, Slot b)
        {
            return !(a == b);
        }

        public bool Equals(Slot other)
        {
            return Number == other.Number;
        }

        public override bool Equals(object obj)
        {
            return obj is Slot other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Number.GetHashCode();
        }
        
        public static implicit operator Slot(ulong value)
        {
            return new Slot(value);
        }

        public static uint Seconds { get; set; } = 12;
    }
}