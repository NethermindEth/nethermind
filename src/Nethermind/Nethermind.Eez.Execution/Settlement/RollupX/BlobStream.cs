// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>
/// The logical byte stream carried by a batch's blobs. Every 32-byte field element holds 31 stream bytes in
/// little-endian significance with a zero top byte, so in the blob it reads <c>00 || reverse(31 bytes)</c>; the
/// blobs of one transaction concatenate in order.
/// </summary>
public static class BlobStream
{
    public const int FieldElementsPerBlob = 4096;
    public const int BytesPerFieldElement = 32;
    public const int DataBytesPerFieldElement = 31;
    public const int BytesPerBlob = FieldElementsPerBlob * BytesPerFieldElement;
    public const int DataBytesPerBlob = FieldElementsPerBlob * DataBytesPerFieldElement;

    /// <exception cref="EezSettlementException">A blob has the wrong size or a field element with a non-zero top byte.</exception>
    public static byte[] Unpack(IReadOnlyList<byte[]> blobs)
    {
        byte[] stream = new byte[blobs.Count * DataBytesPerBlob];
        for (int b = 0; b < blobs.Count; b++)
        {
            Unpack(blobs[b], stream.AsSpan(b * DataBytesPerBlob, DataBytesPerBlob), b);
        }

        return stream;
    }

    /// <summary>Unpacks one physical blob into its exact logical-byte destination.</summary>
    /// <exception cref="EezSettlementException">The blob size or a field element's top byte is invalid.</exception>
    /// <exception cref="ArgumentException">The destination does not have <see cref="DataBytesPerBlob"/> bytes.</exception>
    public static void Unpack(ReadOnlySpan<byte> blob, Span<byte> destination) => Unpack(blob, destination, 0);

    private static void Unpack(ReadOnlySpan<byte> blob, Span<byte> destination, int index)
    {
        if (destination.Length != DataBytesPerBlob) throw new ArgumentException("invalid logical blob destination", nameof(destination));
        if (blob.Length != BytesPerBlob) throw DaReader.Invalid($"blob {index} has {blob.Length} bytes, expected {BytesPerBlob}");
        for (int e = 0; e < FieldElementsPerBlob; e++)
        {
            ReadOnlySpan<byte> element = blob.Slice(e * BytesPerFieldElement, BytesPerFieldElement);
            if (element[0] != 0) throw DaReader.Invalid($"field element {e} of blob {index} has a non-zero top byte");
            Span<byte> data = destination.Slice(e * DataBytesPerFieldElement, DataBytesPerFieldElement);
            element[1..].CopyTo(data);
            data.Reverse();
        }
    }

    /// <summary>The fewest blobs that carry <paramref name="stream"/>, the remainder zero.</summary>
    public static byte[][] Pack(ReadOnlySpan<byte> stream)
    {
        int count = Math.Max(1, (stream.Length + DataBytesPerBlob - 1) / DataBytesPerBlob);
        byte[][] blobs = new byte[count][];
        for (int b = 0; b < count; b++)
        {
            byte[] blob = new byte[BytesPerBlob];
            for (int e = 0; e < FieldElementsPerBlob; e++)
            {
                int start = b * DataBytesPerBlob + e * DataBytesPerFieldElement;
                if (start >= stream.Length)
                {
                    break;
                }

                ReadOnlySpan<byte> data = stream[start..Math.Min(start + DataBytesPerFieldElement, stream.Length)];
                Span<byte> element = blob.AsSpan(e * BytesPerFieldElement + 1, DataBytesPerFieldElement);
                data.CopyTo(element);
                element.Reverse();
            }

            blobs[b] = blob;
        }

        return blobs;
    }
}
