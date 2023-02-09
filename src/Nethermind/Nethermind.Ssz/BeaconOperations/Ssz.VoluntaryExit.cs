// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public const int VoluntaryExitLength = Ssz.EpochLength + Ssz.ValidatorIndexLength + Ssz.BlsSignatureLength;

        public static void Encode(Span<byte> span, VoluntaryExit container)
        {
            if (span.Length != Ssz.VoluntaryExitLength) ThrowTargetLength<VoluntaryExit>(span.Length, Ssz.VoluntaryExitLength);
            if (container == null) return;
            int offset = 0;
            Encode(span, container.Epoch, ref offset);
            Encode(span, container.ValidatorIndex, ref offset);
        }

        public static VoluntaryExit DecodeVoluntaryExit(ReadOnlySpan<byte> span)
        {
            if (span.Length != Ssz.VoluntaryExitLength) ThrowSourceLength<VoluntaryExit>(span.Length, Ssz.VoluntaryExitLength);
            int offset = 0;
            Epoch epoch = DecodeEpoch(span, ref offset);
            ValidatorIndex validatorIndex = DecodeValidatorIndex(span, ref offset);
            VoluntaryExit container = new VoluntaryExit(epoch, validatorIndex);
            return container;
        }

        private static VoluntaryExit DecodeVoluntaryExit(ReadOnlySpan<byte> span, ref int offset)
        {
            VoluntaryExit container =
                DecodeVoluntaryExit(span.Slice(offset, VoluntaryExitLength));
            offset += VoluntaryExitLength;
            return container;
        }

        private static void Encode(Span<byte> span, VoluntaryExit value, ref int offset)
        {
            Encode(span.Slice(offset, VoluntaryExitLength), value);
            offset += VoluntaryExitLength;
        }
    }
}
