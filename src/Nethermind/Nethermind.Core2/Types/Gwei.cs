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

namespace Nethermind.Core2.Types
{
    [DebuggerDisplay("{Amount}")]
    public struct Gwei
    {
        public const int SszLength = sizeof(ulong);

        public static Gwei Zero = default;
        
        public static Gwei One = new Gwei(1);
        
        public Gwei(ulong amount)
        {
            Amount = amount;
        }
        
        public ulong Amount { get; }
        
        public static bool operator <(Gwei a, Gwei b)
        {
            return a.Amount < b.Amount;
        }

        public static bool operator >(Gwei a, Gwei b)
        {
            return a.Amount > b.Amount;
        }
        
        public static bool operator <=(Gwei a, Gwei b)
        {
            return a.Amount <= b.Amount;
        }

        public static bool operator >=(Gwei a, Gwei b)
        {
            return a.Amount >= b.Amount;
        }
        
        public static bool operator ==(Gwei a, Gwei b)
        {
            return a.Amount == b.Amount;
        }

        public static bool operator !=(Gwei a, Gwei b)
        {
            return !(a == b);
        }

        public bool Equals(Gwei other)
        {
            return Amount == other.Amount;
        }

        public override bool Equals(object obj)
        {
            return obj is Gwei other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Amount.GetHashCode();
        }
        
        public static implicit operator Gwei(ulong amount)
        {
            return new Gwei(amount);
        }

        public static Gwei MinDepositAmount { get; set; } = 1_000_000_000UL;

        public static Gwei MaxEffectiveBalance { get; set; } = 32_000_000_000UL;

        public static Gwei EjectionBalance { get; set; } = 16_000_000_000UL;

        public static Gwei EffectiveBalanceIncrement { get; set; } = 1_000_000_000UL;
    }
}