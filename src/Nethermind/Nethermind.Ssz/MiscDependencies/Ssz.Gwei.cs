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
        public const int GweiLength = sizeof(ulong);

        public static void Encode(Span<byte> span, Gwei value)
        {
            Encode(span, value.Amount);
        }

        public static void Encode(Span<byte> span, Gwei[]? value)
        {
            if (value is null)
            {
                return;
            }

            for (int i = 0; i < value.Length; i++)
            {
                Encode(span.Slice(i * Ssz.GweiLength, Ssz.GweiLength), value[i].Amount);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, Gwei value, ref int offset)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(offset), value.Amount);
            offset += Ssz.GweiLength;
        }

        public static Gwei DecodeGwei(Span<byte> span)
        {
            return new Gwei(DecodeULong(span));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Gwei DecodeGwei(ReadOnlySpan<byte> span, ref int offset)
        {
            Gwei gwei = new Gwei(BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset)));
            offset += Ssz.GweiLength;
            return gwei;
        }

        public static Gwei[] DecodeGweis(Span<byte> span)
        {
            if (span.Length == 0)
            {
                return Array.Empty<Gwei>();
            }

            int count = span.Length / Ssz.GweiLength;
            Gwei[] result = new Gwei[count];
            for (int i = 0; i < count; i++)
            {
                Span<byte> current = span.Slice(i * Ssz.GweiLength, Ssz.GweiLength);
                result[i] = DecodeGwei(current);
            }

            return result;
        }
    }
}
