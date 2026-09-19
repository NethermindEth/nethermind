// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Snappier;

namespace Nethermind.BeaconChain.P2P.ReqResp;

/// <summary>Raised when an eth2 req/resp message violates the wire framing or carries an error response.</summary>
public class Eth2ReqRespException(string message, byte responseCode = ReqRespFraming.ResponseCode.InvalidRequest) : Exception(message)
{
    /// <summary>The <see cref="ReqRespFraming.ResponseCode"/> describing the failure.</summary>
    public byte ResponseCode { get; } = responseCode;
}

/// <summary>A single decoded eth2 response chunk.</summary>
/// <param name="Result">The result byte; see <see cref="ReqRespFraming.ResponseCode"/>.</param>
/// <param name="ContextBytes">The 4-byte fork digest for fork-context methods on success chunks; empty otherwise.</param>
/// <param name="Payload">The uncompressed SSZ payload (the <c>ErrorMessage</c> bytes for non-success chunks).</param>
public readonly record struct ResponseChunk(byte Result, byte[] ContextBytes, byte[] Payload);

/// <summary>
/// Pure encode/decode helpers for the eth2 req/resp <c>ssz_snappy</c> wire framing
/// (consensus-specs p2p-interface, "Encoding strategies").
/// </summary>
/// <remarks>
/// A request is <c>varint(ssz_len) ++ snappy_frames(ssz)</c> where the varint is unsigned LEB128 and
/// the snappy bytes use the framing (stream) format, restarted per payload. A response is a sequence
/// of chunks, each <c>result ++ [context-bytes] ++ varint(ssz_len) ++ snappy_frames(ssz)</c>,
/// terminated by stream end. Because each payload is snappy-framed independently but response chunks
/// share one stream, decoding parses the snappy frame headers itself (1-byte type + 3-byte
/// little-endian length) to consume exactly one payload's frames without over-reading into the next
/// chunk, then feeds the collected frames to <see cref="SnappyStream"/> for block decompression and
/// CRC verification. All methods operate on plain <see cref="Stream"/>s and wrap truncation as
/// <see cref="Eth2ReqRespException"/>.
/// </remarks>
public static class ReqRespFraming
{
    /// <summary>Per-chunk result codes (consensus-specs <c>result</c> byte).</summary>
    public static class ResponseCode
    {
        public const byte Success = 0;
        public const byte InvalidRequest = 1;
        public const byte ServerError = 2;
        public const byte ResourceUnavailable = 3;
    }

    /// <summary>The spec <c>MAX_PAYLOAD_SIZE</c>: the maximum uncompressed payload size.</summary>
    public const int MaxPayloadSize = 10 * 1024 * 1024;

    /// <summary>The spec <c>ErrorMessage</c> limit (<c>List[byte, 256]</c>).</summary>
    public const int MaxErrorMessageSize = 256;

    public const int ForkContextLength = 4;

    private const int MaxVarintLength = 10;
    private const byte CompressedFrame = 0x00;
    private const byte UncompressedFrame = 0x01;
    private const byte PaddingFrame = 0xfe;
    private const byte StreamIdentifierFrame = 0xff;
    private const byte FirstSkippableFrame = 0x80;
    // Frame data is capped at the snappy framing-format limits: 65536 bytes of uncompressed data
    // plus the 4-byte CRC, with headroom for the worst-case snappy block expansion of a 64 KiB frame.
    private const int MaxFrameDataLength = 4 + 65536 + 65536 / 6 + 32;
    private static readonly byte[] StreamIdentifierContent = "sNaPpY"u8.ToArray();

    public static async Task WriteRequestAsync(Stream stream, ReadOnlyMemory<byte> ssz, CancellationToken token)
    {
        using MemoryStream buffer = new();
        WriteVarint(buffer, (ulong)ssz.Length);
        using (SnappyStream snappy = new(buffer, CompressionMode.Compress, leaveOpen: true))
        {
            snappy.Write(ssz.Span);
            snappy.Flush();
        }

        await stream.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), token);
    }

    public static async Task<byte[]> ReadRequestAsync(Stream stream, int maxSize, CancellationToken token)
    {
        try
        {
            return await ReadPayloadAsync(stream, maxSize, token);
        }
        catch (EndOfStreamException e)
        {
            throw new Eth2ReqRespException($"Truncated request: {e.Message}");
        }
    }

    public static Task WriteResponseChunkAsync(Stream stream, byte result, ReadOnlyMemory<byte> contextBytes, ReadOnlyMemory<byte> ssz, CancellationToken token)
    {
        using MemoryStream buffer = new();
        buffer.WriteByte(result);
        buffer.Write(contextBytes.Span);
        WriteVarint(buffer, (ulong)ssz.Length);
        using (SnappyStream snappy = new(buffer, CompressionMode.Compress, leaveOpen: true))
        {
            snappy.Write(ssz.Span);
            snappy.Flush();
        }

        return stream.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), token).AsTask();
    }

    public static Task WriteErrorChunkAsync(Stream stream, byte result, string message, CancellationToken token)
    {
        byte[] encoded = Encoding.UTF8.GetBytes(message);
        return WriteResponseChunkAsync(stream, result, default, encoded.AsMemory(0, Math.Min(encoded.Length, MaxErrorMessageSize)), token);
    }

    /// <summary>Reads the next response chunk, or <c>null</c> on a clean end of stream.</summary>
    /// <param name="contextBytesLength">The context-bytes length of the method: <see cref="ForkContextLength"/> for fork-context methods, 0 otherwise.</param>
    /// <param name="maxSize">The maximum uncompressed payload size accepted for a success chunk.</param>
    public static async Task<ResponseChunk?> ReadResponseChunkAsync(Stream stream, int contextBytesLength, int maxSize, CancellationToken token)
    {
        byte[] resultBuffer = new byte[1];
        if (await stream.ReadAsync(resultBuffer, token) == 0)
        {
            return null;
        }

        try
        {
            byte result = resultBuffer[0];
            byte[] contextBytes = [];
            // Context bytes are only present on success chunks; error chunks carry a bare ErrorMessage.
            if (result == ResponseCode.Success && contextBytesLength > 0)
            {
                contextBytes = new byte[contextBytesLength];
                await stream.ReadExactlyAsync(contextBytes, token);
            }

            byte[] payload = await ReadPayloadAsync(stream, result == ResponseCode.Success ? maxSize : MaxErrorMessageSize, token);
            return new ResponseChunk(result, contextBytes, payload);
        }
        catch (EndOfStreamException e)
        {
            throw new Eth2ReqRespException($"Truncated response chunk: {e.Message}");
        }
    }

    private static async Task<byte[]> ReadPayloadAsync(Stream stream, int maxSize, CancellationToken token)
    {
        ulong declaredLength = await ReadVarintAsync(stream, token);
        if (declaredLength == 0 || declaredLength > (ulong)maxSize)
        {
            throw new Eth2ReqRespException($"Invalid payload length {declaredLength}, expected 1..{maxSize}");
        }

        int sszLength = (int)declaredLength;
        using MemoryStream frames = new();
        long uncompressedTotal = 0;
        bool sawStreamIdentifier = false;
        byte[] header = new byte[4];
        while (uncompressedTotal < sszLength)
        {
            await stream.ReadExactlyAsync(header, token);
            byte frameType = header[0];
            int dataLength = header[1] | (header[2] << 8) | (header[3] << 16);
            if (dataLength > MaxFrameDataLength)
            {
                throw new Eth2ReqRespException($"Snappy frame of {dataLength} bytes exceeds the {MaxFrameDataLength} limit");
            }

            byte[] data = new byte[dataLength];
            await stream.ReadExactlyAsync(data, token);

            switch (frameType)
            {
                case StreamIdentifierFrame:
                    if (!data.AsSpan().SequenceEqual(StreamIdentifierContent))
                    {
                        throw new Eth2ReqRespException("Malformed snappy stream identifier frame");
                    }

                    sawStreamIdentifier = true;
                    break;
                case CompressedFrame or UncompressedFrame:
                    if (!sawStreamIdentifier || dataLength < 4)
                    {
                        throw new Eth2ReqRespException("Snappy data frame without stream identifier or CRC");
                    }

                    try
                    {
                        uncompressedTotal += frameType == UncompressedFrame
                            ? dataLength - 4
                            : Snappy.GetUncompressedLength(data.AsSpan(4));
                    }
                    catch (Exception e) when (e is not Eth2ReqRespException)
                    {
                        throw new Eth2ReqRespException($"Malformed snappy block: {e.Message}");
                    }

                    break;
                case PaddingFrame or >= FirstSkippableFrame:
                    continue; // Skippable; do not feed to the decompressor.
                default:
                    throw new Eth2ReqRespException($"Unskippable reserved snappy frame type 0x{frameType:x2}");
            }

            frames.Write(header);
            frames.Write(data);
            if (uncompressedTotal > sszLength)
            {
                throw new Eth2ReqRespException($"Snappy frames decode to {uncompressedTotal} bytes, more than the declared {sszLength}");
            }
        }

        frames.Position = 0;
        byte[] payload = new byte[sszLength];
        try
        {
            using SnappyStream snappy = new(frames, CompressionMode.Decompress);
            snappy.ReadExactly(payload);
        }
        catch (Exception e) when (e is not Eth2ReqRespException)
        {
            throw new Eth2ReqRespException($"Snappy decompression failed: {e.Message}");
        }

        return payload;
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    private static async Task<ulong> ReadVarintAsync(Stream stream, CancellationToken token)
    {
        byte[] buffer = new byte[1];
        ulong value = 0;
        for (int i = 0; i < MaxVarintLength; i++)
        {
            await stream.ReadExactlyAsync(buffer, token);
            value |= (ulong)(buffer[0] & 0x7f) << (7 * i);
            if ((buffer[0] & 0x80) == 0)
            {
                return value;
            }
        }

        throw new Eth2ReqRespException("Varint length header longer than 10 bytes");
    }
}
