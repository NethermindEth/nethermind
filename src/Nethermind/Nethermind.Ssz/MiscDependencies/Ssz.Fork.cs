// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public const int ForkLength = Ssz.ForkVersionLength * 2 + Ssz.EpochLength;

        private static void Encode(Span<byte> span, Fork value, ref int offset)
        {
            Encode(span.Slice(offset, Ssz.ForkLength), value);
            offset += Ssz.ForkLength;
        }

        private static Fork DecodeFork(Span<byte> span, ref int offset)
        {
            Fork fork = DecodeFork(span.Slice(offset, Ssz.ForkLength));
            offset += Ssz.ForkLength;
            return fork;
        }

        public static void Encode(Span<byte> span, Fork? container)
        {
            if (container is null)
            {
                return;
            }

            if (span.Length != Ssz.ForkLength) ThrowTargetLength<Fork>(span.Length, Ssz.ForkLength);
            int offset = 0;
            Encode(span, container.Value.PreviousVersion, ref offset);
            Encode(span, container.Value.CurrentVersion, ref offset);
            Encode(span, container.Value.Epoch, ref offset);
        }

        public static Fork DecodeFork(Span<byte> span)
        {
            if (span.Length != Ssz.ForkLength) ThrowSourceLength<Fork>(span.Length, Ssz.ForkLength);
            int offset = 0;
            ForkVersion previous = DecodeForkVersion(span, ref offset);
            ForkVersion current = DecodeForkVersion(span, ref offset);
            Epoch epoch = DecodeEpoch(span, ref offset);
            return new Fork(previous, current, epoch);
        }

    }
}
