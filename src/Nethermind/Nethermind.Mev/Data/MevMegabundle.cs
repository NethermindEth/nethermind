// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Mev.Data
{
    public partial class MevMegabundle : MevBundle, IEquatable<MevMegabundle>
    {
        public MevMegabundle(long blockNumber, IReadOnlyList<BundleTransaction> transactions, Keccak[]? revertingTxHashes = null,
            Signature? relaySignature = null, UInt256? minTimestamp = null, UInt256? maxTimestamp = null)
            : base(blockNumber, transactions, minTimestamp, maxTimestamp)
        {
            RelaySignature = relaySignature;
            RevertingTxHashes = revertingTxHashes ?? Array.Empty<Keccak>();
            Hash = GetHash(this);
            for (int i = 0; i < transactions.Count; i++)
            {
                transactions[i].BundleHash = Hash;
            }
        }

        public override Keccak Hash { get; }
        public Signature? RelaySignature { get; set; }

        public Address? RelayAddress { get; internal set; }

        public Keccak[] RevertingTxHashes { get; }

        public bool Equals(MevMegabundle? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Equals(Hash, other.Hash)
                   && Equals(RelaySignature, other.RelaySignature);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((MevMegabundle)obj);
        }

        public override int GetHashCode() => HashCode.Combine(Hash, RelaySignature).GetHashCode();

        public override string ToString() =>
            $"Hash:{Hash}; Block:{BlockNumber}; Min:{MinTimestamp}; Max:{MaxTimestamp}; TxCount:{Transactions.Count}; RelaySignature:{RelaySignature};";
    }
}
