// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;

namespace Nethermind.StatelessInputGen;

/// <summary>
/// The Zisk input frame: the data length as a little-endian <see cref="ulong"/>,
/// followed by the data, zero-padded to a multiple of 8 bytes.
/// </summary>
internal static class ZiskFrame
{
    internal const int HeaderLength = sizeof(ulong);

    internal static int GetFrameLength(int dataLength)
    {
        int rem = dataLength % sizeof(ulong);
        return HeaderLength + dataLength + (rem == 0 ? 0 : sizeof(ulong) - rem);
    }

    /// <summary>
    /// Frames <paramref name="dataLength"/> bytes of data already placed at offset <see cref="HeaderLength"/>
    /// in <paramref name="buffer"/> by writing the length header and zeroing the padding.
    /// </summary>
    /// <returns>The total frame length.</returns>
    internal static int FrameInPlace(Span<byte> buffer, int dataLength)
    {
        int frameLength = GetFrameLength(dataLength);

        BinaryPrimitives.WriteUInt64LittleEndian(buffer, (ulong)dataLength);
        // Rented buffers may hold stale bytes where the padding must be zero
        buffer[(HeaderLength + dataLength)..frameLength].Clear();

        return frameLength;
    }

    /// <summary>Wraps the data in a newly allocated frame.</summary>
    internal static byte[] Wrap(ReadOnlySpan<byte> data)
    {
        byte[] frame = new byte[GetFrameLength(data.Length)];

        data.CopyTo(frame.AsSpan(HeaderLength));
        FrameInPlace(frame, data.Length);

        return frame;
    }
}
