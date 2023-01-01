// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Mev.Data
{
    public partial class MevBundle : IEquatable<MevBundle>
    {
        private static int _sequenceNumber = 0;

        public MevBundle(long blockNumber, IReadOnlyList<BundleTransaction> transactions, UInt256? minTimestamp = null, UInt256? maxTimestamp = null)
        {
            Transactions = transactions;
            BlockNumber = blockNumber;

            Hash = GetHash(this);
            for (int i = 0; i < transactions.Count; i++)
            {
                transactions[i].BundleHash = Hash;
            }

            MinTimestamp = minTimestamp ?? UInt256.Zero;
            MaxTimestamp = maxTimestamp ?? UInt256.Zero;
            SequenceNumber = Interlocked.Increment(ref _sequenceNumber);
        }

        public IReadOnlyList<BundleTransaction> Transactions { get; }

        public long BlockNumber { get; }

        public UInt256 MaxTimestamp { get; }

        public UInt256 MinTimestamp { get; }

        public virtual Keccak Hash { get; }

        public int SequenceNumber { get; }

        public bool Equals(MevBundle? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Equals(Hash, other.Hash);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((MevBundle)obj);
        }

        public override int GetHashCode() => Hash.GetHashCode();

        public override string ToString() => $"Hash:{Hash}; Block:{BlockNumber}; Min:{MinTimestamp}; Max:{MaxTimestamp}; TxCount:{Transactions.Count};";
    }
}
