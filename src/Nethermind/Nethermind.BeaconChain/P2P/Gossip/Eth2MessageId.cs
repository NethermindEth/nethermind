// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Snappier;

namespace Nethermind.BeaconChain.P2P.Gossip;

/// <summary>Outcome of capped snappy block decompression of a gossip payload.</summary>
public enum SnappyDecodeResult
{
    Decoded,
    /// <summary>The declared uncompressed length exceeds the cap; the payload was not decompressed.</summary>
    Oversized,
    /// <summary>The payload is not valid snappy block data.</summary>
    Invalid,
}

/// <summary>The eth2 gossipsub message-id function.</summary>
/// <remarks>
/// Per the consensus p2p spec (Altair onward), the message id is the first 20 bytes of
/// <c>SHA256(MESSAGE_DOMAIN_VALID_SNAPPY ++ uint64_le(len(topic)) ++ topic ++ snappy_decompress(message.data))</c>
/// when the payload decompresses within <see cref="MaxGossipSize"/>, and the
/// <c>MESSAGE_DOMAIN_INVALID_SNAPPY</c> variant over the raw payload otherwise. An oversized
/// declared length counts as a failed decompression so that decompression-bomb payloads are never
/// inflated just to compute their id.
/// </remarks>
public static class Eth2MessageId
{
    /// <summary>The spec <c>GOSSIP_MAX_SIZE</c>: the maximum allowed uncompressed gossip payload in bytes.</summary>
    public const int MaxGossipSize = 10 * 1024 * 1024;

    /// <summary>phase0 p2p "Gossipsub size limits": a compressed gossip payload must not exceed <c>max_compressed_len(MAX_PAYLOAD_SIZE)</c>.</summary>
    internal static readonly int MaxCompressedGossipSize = MaxCompressedLength(MaxGossipSize);

    private const int MessageIdLength = 20;

    /// <summary>More than the most a snappy element emits per byte of input: a 3-byte copy of 64 bytes.</summary>
    private const long MaxSnappyExpansion = 22;

    private static ReadOnlySpan<byte> MessageDomainValidSnappy => [0x01, 0x00, 0x00, 0x00];
    private static ReadOnlySpan<byte> MessageDomainInvalidSnappy => [0x00, 0x00, 0x00, 0x00];

    public static byte[] Compute(string topic, ReadOnlySpan<byte> data) =>
        TryDecompress(data, MaxGossipSize, out byte[]? decompressed) == SnappyDecodeResult.Decoded
            ? Hash(MessageDomainValidSnappy, topic, decompressed)
            : Hash(MessageDomainInvalidSnappy, topic, data);

    /// <summary>The spec <c>max_compressed_len(n)</c>: the worst-case snappy-compressed size of a payload of <paramref name="maxUncompressed"/> bytes.</summary>
    /// <remarks>phase0 p2p-interface.md computes <c>32 + n + n // 6</c>; the result is clamped to <see cref="int.MaxValue"/>.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxUncompressed"/> is negative.</exception>
    public static int MaxCompressedLength(int maxUncompressed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxUncompressed);
        return (int)Math.Min(32L + maxUncompressed + maxUncompressed / 6, int.MaxValue);
    }

    /// <summary>Snappy block decompression with an uncompressed-size cap, as used for gossip payloads.</summary>
    public static SnappyDecodeResult TryDecompress(ReadOnlySpan<byte> data, int maxSize, out byte[]? decompressed)
    {
        decompressed = null;
        try
        {
            int length = Snappy.GetUncompressedLength(data);
            if ((uint)length > (uint)maxSize)
            {
                return SnappyDecodeResult.Oversized;
            }

            // A declared length the input cannot expand to never decodes; refusing it first keeps a few bytes from buying that buffer.
            if (length > data.Length * MaxSnappyExpansion)
            {
                return SnappyDecodeResult.Invalid;
            }

            byte[] buffer = new byte[length];
            Snappy.Decompress(data, buffer);
            decompressed = buffer;
            return SnappyDecodeResult.Decoded;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException)
        {
            return SnappyDecodeResult.Invalid;
        }
    }

    private static byte[] Hash(ReadOnlySpan<byte> domain, string topic, ReadOnlySpan<byte> payload)
    {
        byte[] topicBytes = Encoding.UTF8.GetBytes(topic);
        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(domain);
        Span<byte> topicLength = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(topicLength, (ulong)topicBytes.Length);
        sha.AppendData(topicLength);
        sha.AppendData(topicBytes);
        sha.AppendData(payload);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        sha.GetHashAndReset(hash);
        return hash[..MessageIdLength].ToArray();
    }
}
