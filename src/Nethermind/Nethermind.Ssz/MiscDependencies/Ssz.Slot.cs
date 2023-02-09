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
        public const int SlotLength = sizeof(ulong);

        public static void Encode(Span<byte> span, Slot value)
        {
            Encode(span, value.Number);
        }

        public static Slot DecodeSlot(Span<byte> span)
        {
            return new Slot(DecodeULong(span));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Slot DecodeSlot(ReadOnlySpan<byte> span, ref int offset)
        {
            Slot slot = new Slot(BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset)));
            offset += Ssz.SlotLength;
            return slot;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, Slot value, ref int offset)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(offset), value.Number);
            offset += Ssz.SlotLength;
        }
    }
}
