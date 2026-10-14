// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;

namespace Nethermind.Serialization.Ssz;

public static partial class Ssz
{
    /// <summary>Decodes one SSZ value, consolidating segmented input into a temporary buffer.</summary>
    public static void Decode<T>(ReadOnlySequence<byte> data, out T value) where T : ISszCodec<T>
    {
        if (data.IsSingleSegment)
        {
            T.Decode(data.FirstSpan, out value);
            return;
        }
        int length = checked((int)data.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            data.CopyTo(rented.AsSpan(0, length));
            T.Decode(rented.AsSpan(0, length), out value);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
