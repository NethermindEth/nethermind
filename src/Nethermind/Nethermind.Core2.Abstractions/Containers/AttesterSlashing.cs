// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core2.Containers
{
    public class AttesterSlashing
    {
        public static readonly AttesterSlashing Zero = new AttesterSlashing(IndexedAttestation.Zero, IndexedAttestation.Zero);

        public AttesterSlashing(
            IndexedAttestation attestation1,
            IndexedAttestation attestation2)
        {
            Attestation1 = attestation1;
            Attestation2 = attestation2;
        }

        public IndexedAttestation Attestation1 { get; }
        public IndexedAttestation Attestation2 { get; }

        public override string ToString()
        {
            return $"A1:({Attestation1}) A2:({Attestation2})";
        }

        public bool Equals(AttesterSlashing other)
        {
            return Equals(Attestation1, other.Attestation1) &&
                   Equals(Attestation2, other.Attestation2);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj is AttesterSlashing other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Attestation1, Attestation2);
        }
    }
}
