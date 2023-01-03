// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public const int ProposerSlashingLength = Ssz.ValidatorIndexLength + 2 * Ssz.SignedBeaconBlockHeaderLength;

        public static void Encode(Span<byte> span, ProposerSlashing container)
        {
            if (span.Length != Ssz.ProposerSlashingLength)
            {
                ThrowTargetLength<ProposerSlashing>(span.Length, Ssz.ProposerSlashingLength);
            }

            int offset = 0;
            Encode(span.Slice(0, Ssz.ValidatorIndexLength), container.ProposerIndex);
            offset += Ssz.ValidatorIndexLength;
            Encode(span.Slice(offset, Ssz.SignedBeaconBlockHeaderLength), container.SignedHeader1);
            offset += Ssz.SignedBeaconBlockHeaderLength;
            Encode(span.Slice(offset, Ssz.SignedBeaconBlockHeaderLength), container.SignedHeader2);
        }

        public static ProposerSlashing DecodeProposerSlashing(ReadOnlySpan<byte> span)
        {
            if (span.Length != Ssz.ProposerSlashingLength) ThrowSourceLength<ProposerSlashing>(span.Length, Ssz.ProposerSlashingLength);
            int offset = 0;
            ValidatorIndex proposerIndex = DecodeValidatorIndex(span, ref offset);
            SignedBeaconBlockHeader signedHeader1 = DecodeSignedBeaconBlockHeader(span, ref offset);
            SignedBeaconBlockHeader signedHeader2 = DecodeSignedBeaconBlockHeader(span, ref offset);
            ProposerSlashing container = new ProposerSlashing(proposerIndex, signedHeader1, signedHeader2);
            return container;
        }

        public static void Encode(Span<byte> span, ProposerSlashing[] containers)
        {
            if (span.Length != Ssz.ProposerSlashingLength * containers.Length)
            {
                ThrowTargetLength<ProposerSlashing>(span.Length, Ssz.ProposerSlashingLength);
            }

            for (int i = 0; i < containers.Length; i++)
            {
                Encode(span.Slice(i * Ssz.ProposerSlashingLength, Ssz.ProposerSlashingLength), containers[i]);
            }
        }

        public static ProposerSlashing[] DecodeProposerSlashings(ReadOnlySpan<byte> span)
        {
            if (span.Length % Ssz.ProposerSlashingLength != 0)
            {
                ThrowInvalidSourceArrayLength<ProposerSlashing>(span.Length, Ssz.ProposerSlashingLength);
            }

            int count = span.Length / Ssz.ProposerSlashingLength;
            ProposerSlashing[] containers = new ProposerSlashing[count];
            for (int i = 0; i < count; i++)
            {
                containers[i] = DecodeProposerSlashing(span.Slice(i * Ssz.ProposerSlashingLength, Ssz.ProposerSlashingLength));
            }

            return containers;
        }
    }
}
