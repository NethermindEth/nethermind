// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Crypto;

public class SignatureConverter : JsonConverter<Signature>
{
    public override Signature? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString() is { } hex ? new Signature(hex) : null;

    [SkipLocalsInit]
    public override void Write(Utf8JsonWriter writer, Signature value, JsonSerializerOptions options)
    {
        Unsafe.SkipInit(out RawBuffer152 rawBuf);
        Span<byte> buf = MemoryMarshal.CreateSpan(ref Unsafe.As<RawBuffer152, byte>(ref rawBuf), 152);

        buf[0] = (byte)'"';
        Unsafe.WriteUnaligned(ref buf[1], (ushort)0x7830); // "0x" little-endian
        value.Bytes.OutputBytesToByteHex(buf.Slice(3, 128), extraNibble: false);

        ulong v = value.V;
        int vNibbles = v == 0 ? 2 : (67 - BitOperations.LeadingZeroCount(v)) >> 2;
        if ((vNibbles & 1) != 0) vNibbles++; // pad to even length for Signature.ToString() compatibility

        for (int i = vNibbles - 1; i >= 0; i--)
        {
            int nibble = (int)(v & 0xF);
            buf[131 + i] = (byte)(nibble < 10 ? '0' + nibble : 'a' + nibble - 10);
            v >>= 4;
        }

        buf[131 + vNibbles] = (byte)'"';

        writer.WriteRawValue(buf.Slice(0, 132 + vNibbles), skipInputValidation: true);
    }

    /// <summary>
    /// 152-byte inline buffer (19 × ulong) — InlineArray avoids the GS cookie overhead
    /// the JIT adds for stackalloc, mirroring the HexBuffer pattern in HexWriter.
    /// </summary>
    [InlineArray(19)]
    private struct RawBuffer152
    {
        private ulong _element0;
    }
}
