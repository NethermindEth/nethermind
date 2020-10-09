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
// 

using System;
using System.Collections.Generic;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using DestinationTuple = System.ValueTuple<Nethermind.Core.Address, byte[], Nethermind.Int256.UInt256>;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public partial class TxPriorityContract
    {
        public readonly struct Destination : IEquatable<Destination>
        {
            public static byte[] FnSignatureEmpty = new byte[4];

            public Destination(Address target, byte[] fnSignature, UInt256 value)
            {
                Target = target;
                FnSignature = fnSignature;
                Value = value;
            }

            public Address Target { get; }
            public byte[] FnSignature { get; }
            public UInt256 Value { get; }

            public static implicit operator Destination(DestinationTuple tuple) => 
                new Destination(tuple.Item1, tuple.Item2, tuple.Item3);

            public static implicit operator DestinationTuple(Destination destination) => 
                (destination.Target, destination.FnSignature, destination.Value);
            
            public static implicit operator Destination(Transaction tx) => GetTransactionKey(tx);

            public static Destination GetTransactionKey(Transaction tx)
            {
                byte[] fnSignature = tx.Data.Length >= 4 ? AbiSignature.GetAddress(tx.Data) : FnSignatureEmpty;
                return new Destination(tx.To, fnSignature,UInt256.Zero);
            }

            public bool Equals(Destination other) =>
                Equals(Target, other.Target) && Equals(FnSignature, other.FnSignature);

            public override bool Equals(object obj) => obj is Destination other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(Target, FnSignature);
        }

        public class DestinationMethodComparer : IComparer<Destination>, IEqualityComparer<Destination>
        {
            public static readonly DestinationMethodComparer Instance = new DestinationMethodComparer();

            public int Compare(Destination x, Destination y)
            {
                bool sameTargetMethod = SameTargetMethod(x, y);
                return sameTargetMethod 
                    ? 0 // if same method, we want to treat destinations as same - to be unique 
                    : y.Value.CompareTo(x.Value); // if not we want to sort destinations by priority descending order
            }

            private static bool SameTargetMethod(in Destination x, in Destination y)
            {
                int targetComparison = Comparer<Address>.Default.Compare(x.Target, y.Target);

                if (targetComparison == 0)
                {
                    targetComparison = Bytes.Comparer.Compare(x.FnSignature, y.FnSignature);
                }

                return targetComparison == 0;
            }

            public bool Equals(Destination x, Destination y) => 
                Equals(x.Target, y.Target) && Bytes.EqualityComparer.Equals(x.FnSignature, y.FnSignature);

            public int GetHashCode(Destination obj) => HashCode.Combine(obj.Target, Bytes.EqualityComparer.GetHashCode(obj.FnSignature));
        }        
    }
}
