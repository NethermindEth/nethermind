// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    public class IndexedAttestation
    {
        private List<ValidatorIndex> _attestingIndices;

        public static readonly IndexedAttestation Zero =
            new IndexedAttestation(new ValidatorIndex[0], AttestationData.Zero, BlsSignature.Zero);

        public IndexedAttestation(
            IEnumerable<ValidatorIndex> attestingIndices,
            AttestationData data,
            BlsSignature signature)
        {
            _attestingIndices = new List<ValidatorIndex>(attestingIndices);
            Data = data;
            Signature = signature;
        }

        public IReadOnlyList<ValidatorIndex> AttestingIndices => _attestingIndices;

        public AttestationData Data { get; }

        public BlsSignature Signature { get; }

        public override string ToString()
        {
            return $"C:{Data.Index} S:{Data.Slot} Sig:{Signature.ToString().Substring(0, 12)}";
        }

        public bool Equals(IndexedAttestation other)
        {
            if (!Equals(Data, other.Data) ||
                !Equals(Signature, other.Signature) ||
                AttestingIndices.Count != other.AttestingIndices.Count)
            {
                return false;
            }

            for (int i = 0; i < AttestingIndices.Count; i++)
            {
                if (AttestingIndices[i] != other.AttestingIndices[i])
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
            return obj is IndexedAttestation other && Equals(other);
        }

        public override int GetHashCode()
        {
            var hashCode = new HashCode();
            hashCode.Add(Data);
            hashCode.Add(Signature);
            for (int i = 0; i < AttestingIndices.Count; i++)
            {
                hashCode.Add(AttestingIndices[i]);
            }

            return hashCode.ToHashCode();
        }

    }
}
