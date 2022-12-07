// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public const int CommitteeIndexLength = sizeof(ulong);

        public static void Encode(Span<byte> span, CommitteeIndex value)
        {
            Encode(span, value.Number);
        }

        public static CommitteeIndex DecodeCommitteeIndex(Span<byte> span)
        {
            return new CommitteeIndex(DecodeULong(span));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static CommitteeIndex DecodeCommitteeIndex(ReadOnlySpan<byte> span, ref int offset)
        {
            CommitteeIndex committeeIndex = new CommitteeIndex(BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset, Ssz.CommitteeIndexLength))); BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset, Ssz.CommitteeIndexLength));
            offset += Ssz.CommitteeIndexLength;
            return committeeIndex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, CommitteeIndex value, ref int offset)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(offset), value.Number);
            offset += Ssz.CommitteeIndexLength;
        }

    }
}
