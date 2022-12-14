// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Linq;
using Nethermind.Core2.Crypto;

namespace Nethermind.Core2.Containers
{
    public class Attestation : IEquatable<Attestation>
    {
        public static readonly Attestation Zero = new Attestation(new BitArray(0), AttestationData.Zero, BlsSignature.Zero);

        public Attestation(BitArray aggregationBits, AttestationData data, BlsSignature signature)
        {
            AggregationBits = aggregationBits;
            Data = data;
            Signature = signature;
        }

        public BitArray AggregationBits { get; }

        public AttestationData Data { get; }

        public BlsSignature Signature { get; private set; }

        public void SetSignature(BlsSignature signature)
        {
            Signature = signature;
        }

        public override string ToString()
        {
            return $"C:{Data.Index} S:{Data.Slot} Sig:{Signature.ToString().Substring(0, 12)}";
        }

        public bool Equals(Attestation? other)
        {
            if (other is null ||
                !Equals(Data, other.Data) ||
                !Equals(Signature, other.Signature) ||
                AggregationBits.Count != other.AggregationBits.Count)
            {
                return false;
            }

            for (int i = 0; i < AggregationBits.Count; i++)
            {
                if (AggregationBits[i] != other.AggregationBits[i])
                {
                    return false;
                }
            }

            return true;

        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj is Attestation other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(AggregationBits, Data, Signature);
        }
    }
}
