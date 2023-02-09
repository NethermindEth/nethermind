// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core2;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public const int EpochLength = sizeof(ulong);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Encode(Span<byte> span, Epoch value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(span, value.Number);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Epoch DecodeEpoch(ReadOnlySpan<byte> span, ref int offset)
        {
            Epoch epoch = new Epoch(BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset)));
            offset += Ssz.EpochLength;
            return epoch;
        }

        public static Epoch DecodeEpoch(Span<byte> span)
        {
            return new Epoch(BinaryPrimitives.ReadUInt64LittleEndian(span));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, Epoch value, ref int offset)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(offset), value.Number);
            offset += Ssz.EpochLength;
        }
    }
}
