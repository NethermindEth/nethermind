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
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    public class Deposit
    {
        private readonly List<Bytes32> _proof;

        public Deposit(IEnumerable<Bytes32> proof, Ref<DepositData> data)
        {
            _proof = new List<Bytes32>(proof);
            Data = data;
        }

        public Ref<DepositData> Data { get; } 

        [DebuggerHidden]
        public IReadOnlyList<Bytes32> Proof => _proof.AsReadOnly();

        public override string ToString()
        {           
            return $"I:{Proof[^1].ToString().Substring(0, 12)} P:{Data.Item.PublicKey.ToString().Substring(0, 12)} A:{Data.Item.Amount}";
        }
        
        public bool Equals(Deposit other)
        {
            return Data.Equals(other.Data)
                && Proof.SequenceEqual(other.Proof);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj is Deposit other && Equals(other);
        }

        public override int GetHashCode()
        {
            HashCode hashCode = new HashCode();
            foreach (Bytes32 value in Proof)
            {
                hashCode.Add(value);
            }
            hashCode.Add(Data);
            return hashCode.ToHashCode();
        }
    }
}
