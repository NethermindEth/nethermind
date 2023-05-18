// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Abi;
using Nethermind.Consensus.AuRa.Contracts.DataStore;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using DestinationTuple = System.ValueTuple<Nethermind.Core.Address, byte[], Nethermind.Int256.UInt256>;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public partial class TxPriorityContract
    {
        public enum DestinationSource
        {
            Local,
            Contract
        }

        public struct Destination : IEqualityComparer<Destination>
        {
            public static byte[] FnSignatureEmpty = new byte[4];

            public Destination(
                Address target,
                byte[] fnSignature,
                UInt256 value,
                DestinationSource source = DestinationSource.Contract,
                long blockNumber = 0)
            {
                Target = target;
                FnSignature = fnSignature;
                Value = value;
                Source = source;
                BlockNumber = blockNumber;
            }

            public Address Target { get; set; }
            public byte[] FnSignature { get; set; }
            public UInt256 Value { get; set; }
            public long BlockNumber { get; set; }
            public DestinationSource Source { get; set; }

            public static Destination FromAbiTuple(DestinationTuple tuple, long blockNumber) =>
                new Destination(tuple.Item1, tuple.Item2, tuple.Item3, DestinationSource.Contract, blockNumber);

            public static implicit operator DestinationTuple(Destination destination) =>
                (destination.Target, destination.FnSignature, destination.Value);

            public static implicit operator Destination(Transaction tx) => GetTransactionKey(tx);

            public static Destination GetTransactionKey(Transaction tx)
            {
                byte[] fnSignature = tx.Data?.Length >= 4 ? AbiSignature.GetAddress(tx.Data.FasterToArray()) : FnSignatureEmpty;
                return new Destination(tx.To, fnSignature, UInt256.Zero);
            }

            public bool Equals(Destination x, Destination y) => Equals(x.Target, y.Target) && Equals(x.FnSignature, y.FnSignature);

            public int GetHashCode(Destination obj) => HashCode.Combine(obj.Target, obj.FnSignature);

            public override string ToString() => $"{Target}.{FnSignature.ToHexString()}={Value}@{Source}.{BlockNumber}";
        }

        public class ValueDestinationMethodComparer : IComparer<Destination>
        {
            public static readonly ValueDestinationMethodComparer Instance = new ValueDestinationMethodComparer();

            public int Compare(Destination x, Destination y)
            {
                // locals have higher priority than non-local values
                int targetComparison = x.Source.CompareTo(y.Source);
                if (targetComparison != 0) return targetComparison;

                // then if value is overridden in later block we want the value from later block
                targetComparison = y.BlockNumber.CompareTo(x.BlockNumber);
                if (targetComparison != 0) return targetComparison;

                // we want to sort destinations by priority descending order
                return y.Value.CompareTo(x.Value);
            }
        }

        public class DistinctDestinationMethodComparer : IComparer<Destination>, IEqualityComparer<Destination>
        {
            public static readonly DistinctDestinationMethodComparer Instance = new DistinctDestinationMethodComparer();

            public int Compare(Destination x, Destination y)
            {
                // if same method, we want to treat destinations as same - to be unique
                int targetComparison = Comparer<Address>.Default.Compare(x.Target, y.Target);
                if (targetComparison != 0) return targetComparison;
                return Bytes.Comparer.Compare(x.FnSignature, y.FnSignature);
            }

            public bool Equals(Destination x, Destination y) =>
                Equals(x.Target, y.Target) && Bytes.EqualityComparer.Equals(x.FnSignature, y.FnSignature);

            public int GetHashCode(Destination obj) => HashCode.Combine(obj.Target, Bytes.EqualityComparer.GetHashCode(obj.FnSignature));
        }

        public class DestinationSortedListContractDataStoreCollection : SortedListContractDataStoreCollection<Destination>
        {
            public DestinationSortedListContractDataStoreCollection()
                : base(DistinctDestinationMethodComparer.Instance, ValueDestinationMethodComparer.Instance)
            {
            }

        }
    }
}
