// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P.Gossip;
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
    // A requester must half-close after its request; one that keeps the stream open is served once this passes with no further byte.
    private static readonly TimeSpan TrailingBytesGrace = TimeSpan.FromMilliseconds(500);
    // phase0 p2p ssz_snappy: max_compressed_len(0); a stream identifier (10 bytes) and one empty data frame (at most 9) fit.
    private static readonly int MaxEmptyRequestFramingBytes = Eth2MessageId.MaxCompressedLength(0);
    private static readonly byte[] StreamIdentifierContent = "sNaPpY"u8.ToArray();
    // The masked CRC-32C of no data (0xa282ead8), little-endian, as the snappy framing format stores it.
    private static readonly byte[] EmptyDataChecksum = [0xd8, 0xea, 0x82, 0xa2];

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

    /// <param name="allowEmpty">Whether the request SSZ type has a zero minimum size (a bare list), so a zero length-prefix is within its bounds.</param>
    public static async Task<byte[]> ReadRequestAsync(Stream stream, int maxSize, CancellationToken token, bool allowEmpty = false)
    {
        try
        {
            ulong declaredLength = await ReadVarintAsync(stream, token);
            if (declaredLength == 0 && allowEmpty)
            {
                await ReadEmptyRequestFramingAsync(stream, token);
                return [];
            }

            return await ReadFramedPayloadAsync(stream, declaredLength, maxSize, token, isRequest: true);
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

    /// <summary>Throws <see cref="Eth2ReqRespException"/> when the requester sends a byte where its request has ended.</summary>
    /// <remarks>phase0 p2p ssz_snappy: bytes remaining after the n SSZ bytes are invalid input. Reads one byte, inside <paramref name="token"/>'s deadline, so it never waits for EOF.</remarks>
    internal static async Task RejectTrailingBytesAsync(Stream stream, CancellationToken token)
    {
        if (await TryReadByteWithinGraceAsync(stream, new byte[1], token))
        {
            throw new Eth2ReqRespException("Unexpected bytes after the end of the request");
        }
    }

    private static async Task<bool> TryReadByteWithinGraceAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        using CancellationTokenSource grace = CancellationTokenSource.CreateLinkedTokenSource(token);
        grace.CancelAfter(TrailingBytesGrace);
        try
        {
            return await stream.ReadAsync(buffer.AsMemory(0, 1), grace.Token) != 0;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return false;
        }
    }

    // An empty payload has no data frame to end it, so its framing runs to the requester's half-close, to max_compressed_len(0) or to the first data frame; any legal framing that decodes to no bytes is accepted.
    private static async Task ReadEmptyRequestFramingAsync(Stream stream, CancellationToken token)
    {
        using MemoryStream frames = new();
        byte[] header = new byte[4];
        int framedBytesRead = 0;
        bool sawDataFrame = false;
        // phase0 p2p ssz_snappy: nothing past max_compressed_len(0) is read, so framing that fills the bound ends there.
        while (!sawDataFrame && framedBytesRead < MaxEmptyRequestFramingBytes && await stream.ReadAsync(header.AsMemory(0, 1), token) != 0)
        {
            if (framedBytesRead + header.Length > MaxEmptyRequestFramingBytes)
            {
                throw new Eth2ReqRespException($"Snappy framing of an empty request exceeds the {MaxEmptyRequestFramingBytes}-byte bound");
            }

            await stream.ReadExactlyAsync(header.AsMemory(1), token);
            byte frameType = header[0];
            int dataLength = header[1] | (header[2] << 8) | (header[3] << 16);
            framedBytesRead += 4 + dataLength;
            if (framedBytesRead > MaxEmptyRequestFramingBytes)
            {
                throw new Eth2ReqRespException($"Snappy framing of an empty request exceeds the {MaxEmptyRequestFramingBytes}-byte bound");
            }

            byte[] data = new byte[dataLength];
            await stream.ReadExactlyAsync(data, token);
            if (frames.Length == 0 && frameType != StreamIdentifierFrame)
            {
                throw new Eth2ReqRespException("Snappy framing of an empty request does not start with a stream identifier");
            }

            switch (frameType)
            {
                case StreamIdentifierFrame:
                    if (!data.AsSpan().SequenceEqual(StreamIdentifierContent))
                    {
                        throw new Eth2ReqRespException("Malformed snappy stream identifier frame");
                    }

                    break;
                case CompressedFrame or UncompressedFrame:
                    sawDataFrame = true;
                    // Only an empty frame carries this checksum; the decompressor below rejects any data, but skips the checksum of an empty uncompressed frame.
                    if (data.Length < 4 || !data.AsSpan(0, 4).SequenceEqual(EmptyDataChecksum))
                    {
                        throw new Eth2ReqRespException("Snappy data frame of an empty request without the empty-data checksum");
                    }

                    // An empty snappy block is a zero length varint, canonical or not, and nothing else; the decompressor ignores bytes after it.
                    if (frameType == CompressedFrame && !IsEmptySnappyBlock(data.AsSpan(4)))
                    {
                        throw new Eth2ReqRespException("Compressed snappy frame of an empty request is not one empty block");
                    }

                    break;
                case PaddingFrame or >= FirstSkippableFrame:
                    continue;
                default:
                    throw new Eth2ReqRespException($"Unskippable reserved snappy frame type 0x{frameType:x2}");
            }

            frames.Write(header);
            frames.Write(data);
        }

        frames.Position = 0;
        try
        {
            using SnappyStream snappy = new(frames, CompressionMode.Decompress);
            if (snappy.Read(new byte[1]) != 0)
            {
                throw new Eth2ReqRespException("Snappy frames of an empty request decode to data");
            }
        }
        catch (Exception e) when (e is not Eth2ReqRespException)
        {
            throw new Eth2ReqRespException($"Snappy decompression failed: {e.Message}");
        }

        // The data frame ends the request, so any frame byte after it is refused unless the bound is already used.
        if (sawDataFrame && framedBytesRead < MaxEmptyRequestFramingBytes)
        {
            await RejectTrailingBytesAsync(stream, token);
        }
    }

    private static bool IsEmptySnappyBlock(ReadOnlySpan<byte> block)
    {
        // A zero uncompressed length is continuation bits only before its last byte; the decompressor bounds the varint length.
        if (block.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < block.Length - 1; i++)
        {
            if (block[i] != 0x80)
            {
                return false;
            }
        }

        return block[^1] == 0;
    }

    private static async Task<byte[]> ReadPayloadAsync(Stream stream, int maxSize, CancellationToken token) =>
        await ReadFramedPayloadAsync(stream, await ReadVarintAsync(stream, token), maxSize, token);

    private static async Task<byte[]> ReadFramedPayloadAsync(Stream stream, ulong declaredLength, int maxSize, CancellationToken token, bool isRequest = false)
    {
        if (declaredLength == 0 || declaredLength > (ulong)maxSize)
        {
            throw new Eth2ReqRespException($"Invalid payload length {declaredLength}, expected 1..{maxSize}");
        }

        int sszLength = (int)declaredLength;
        // phase0 p2p ssz_snappy: a reader MUST NOT read more than max_compressed_len(n) bytes after the length-prefix n, frame headers and padding included.
        int maxFramedBytes = Eth2MessageId.MaxCompressedLength(sszLength);
        long framedBytesRead = 0;
        using MemoryStream frames = new();
        long uncompressedTotal = 0;
        bool sawStreamIdentifier = false;
        byte[] header = new byte[4];
        while (uncompressedTotal < sszLength)
        {
            if (framedBytesRead + header.Length > maxFramedBytes)
            {
                throw new Eth2ReqRespException($"Snappy framing for a {sszLength}-byte payload needs more than the {maxFramedBytes}-byte bound");
            }

            await stream.ReadExactlyAsync(header, token);
            byte frameType = header[0];
            int dataLength = header[1] | (header[2] << 8) | (header[3] << 16);
            if (dataLength > MaxFrameDataLength)
            {
                throw new Eth2ReqRespException($"Snappy frame of {dataLength} bytes exceeds the {MaxFrameDataLength} limit");
            }

            framedBytesRead += header.Length + dataLength;
            if (framedBytesRead > maxFramedBytes)
            {
                throw new Eth2ReqRespException($"Snappy framing for a {sszLength}-byte payload would read {framedBytesRead} wire bytes, more than the {maxFramedBytes}-byte bound");
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

        // The reader must not read past max_compressed_len(n), so a payload that used the whole bound is not probed.
        if (isRequest && framedBytesRead < maxFramedBytes)
        {
            await RejectTrailingBytesAsync(stream, token);
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
