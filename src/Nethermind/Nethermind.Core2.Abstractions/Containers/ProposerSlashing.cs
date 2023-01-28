// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Containers
{
    public class ProposerSlashing
    {
        public static readonly ProposerSlashing Zero = new ProposerSlashing(ValidatorIndex.Zero,
            SignedBeaconBlockHeader.Zero, SignedBeaconBlockHeader.Zero);

        public ProposerSlashing(
            ValidatorIndex proposerIndex,
            SignedBeaconBlockHeader signedHeader1,
            SignedBeaconBlockHeader signedHeader2)
        {
            ProposerIndex = proposerIndex;
            SignedHeader1 = signedHeader1;
            SignedHeader2 = signedHeader2;
        }

        public SignedBeaconBlockHeader SignedHeader1 { get; }
        public SignedBeaconBlockHeader SignedHeader2 { get; }
        public ValidatorIndex ProposerIndex { get; }

        public override string ToString()
        {
            return $"P:{ProposerIndex} for B1:({SignedHeader1})";
        }

        public bool Equals(ProposerSlashing other)
        {
            return ProposerIndex == other.ProposerIndex &&
                   Equals(SignedHeader1, other.SignedHeader1) &&
                   Equals(SignedHeader2, other.SignedHeader2);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj is ProposerSlashing other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(ProposerIndex, SignedHeader1, SignedHeader2);
        }
    }
}
