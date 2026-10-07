// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Test;

internal static class TestWords
{
    /// <summary>A distinct 32-byte word: <paramref name="value"/> big-endian in the last eight bytes.</summary>
    public static ValueHash256 Word(ulong value)
    {
        byte[] bytes = new byte[32];
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(24), value);
        return new ValueHash256(bytes);
    }
}
