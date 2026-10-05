// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.IO.Compression;
using System.Text;
using Nethermind.Network.Libp2p;
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
    public static async Task<byte[]> ReadRequestAsync(Stream stream, int maxSize, CancellationToken token, bool allowEmpty = false) =>
        (await ReadRequestWithTailAsync(stream, maxSize, token, allowEmpty)).Payload;

    /// <summary>Reads a request as complete once its payload and framing are in, without waiting for the requester's EOF.</summary>
    /// <returns>The payload, and the rest of the stream to watch while the request is served.</returns>
    /// <exception cref="Eth2ReqRespException">The request is malformed, or bytes after it are already buffered.</exception>
    internal static async Task<(byte[] Payload, RequestTail Tail)> ReadRequestWithTailAsync(Stream stream, int maxSize, CancellationToken token, bool allowEmpty = false)
    {
        try
        {
            ulong declaredLength = await ReadVarintAsync(stream, token);
            if (declaredLength == 0 && allowEmpty)
            {
                return ([], await ReadEmptyRequestFramingAsync(stream, token));
            }

            (byte[] payload, bool tailOpen) = await ReadFramedPayloadAsync(stream, declaredLength, maxSize, token, isRequest: true);
            return (payload, tailOpen ? RequestTail.Open : RequestTail.Closed);
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
    public static async Task<ResponseChunk?> ReadResponseChunkAsync(Stream input, int contextBytesLength, int maxSize, CancellationToken token)
    {
        byte[] resultBuffer = new byte[1];
        if (await input.ReadAsync(resultBuffer, token) == 0)
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
                await input.ReadExactlyAsync(contextBytes, token);
            }

            ulong declaredLength = await ReadVarintAsync(input, token);
            byte[] payload;
            if (result != ResponseCode.Success && declaredLength == 0)
            {
                RequestTail tail = await ReadEmptyRequestFramingAsync(input, token);
                if (!tail.IsClosed && await tail.WatchAsync(input, token) is { } error)
                {
                    throw new Eth2ReqRespException(error);
                }

                payload = [];
            }
            else
            {
                payload = (await ReadFramedPayloadAsync(input, declaredLength, result == ResponseCode.Success ? maxSize : MaxErrorMessageSize, token)).Payload;
            }

            return new ResponseChunk(result, contextBytes, payload);
        }
        catch (EndOfStreamException e)
        {
            throw new Eth2ReqRespException($"Truncated response chunk: {e.Message}");
        }
    }

    /// <summary>Throws <see cref="Eth2ReqRespException"/> when a byte is already buffered where the request has ended.</summary>
    /// <remarks>phase0 p2p ssz_snappy: bytes remaining after the n SSZ bytes are invalid input. Never waits for a byte: one that arrives later is left to <see cref="RequestTail"/>.</remarks>
    internal static async Task RejectTrailingBytesAsync(Stream stream, CancellationToken token)
    {
        if (await TryReadBufferedByteAsync(stream, new byte[1], token))
        {
            throw new Eth2ReqRespException("Unexpected bytes after the end of the request");
        }
    }

    // Only a channel can tell a buffered byte from one still to come; an in-memory stream never waits.
    private static async ValueTask<bool> TryReadBufferedByteAsync(Stream stream, byte[] buffer, CancellationToken token) =>
        stream is ChannelStreamAdapter channel
            ? await channel.TryReadBufferedByteAsync(buffer, token)
            : await stream.ReadAsync(buffer.AsMemory(0, 1), token) != 0;

    /// <summary>What is left of a request stream once its payload is read: the frames an empty request may still owe, then nothing.</summary>
    /// <remarks>phase0 p2p ssz_snappy: any byte past the framed payload is invalid input, but a request is served without waiting for the requester's EOF, so the stream is watched while serving.</remarks>
    internal sealed class RequestTail(EmptyRequestFraming? framing = null)
    {
        internal static readonly RequestTail Open = new();
        internal static readonly RequestTail Closed = new();

        /// <summary>Whether the request used its whole framing bound, after which nothing is read.</summary>
        internal bool IsClosed => ReferenceEquals(this, Closed);

        /// <summary>Waits for the requester to end its stream or to send what it must not.</summary>
        /// <returns>What the requester did wrong, or <c>null</c> when it ended the stream cleanly.</returns>
        internal async Task<string?> WatchAsync(Stream stream, CancellationToken token)
        {
            byte[] buffer = new byte[1];
            try
            {
                if (framing is not null)
                {
                    while (!framing.IsComplete)
                    {
                        if (await stream.ReadAsync(buffer.AsMemory(0, 1), token) == 0)
                        {
                            return null;
                        }

                        await framing.ReadFrameAsync(stream, buffer[0], token);
                    }

                    framing.Validate();
                    if (!framing.ExpectsNoMoreBytes)
                    {
                        return null;
                    }
                }

                return await stream.ReadAsync(buffer.AsMemory(0, 1), token) == 0 ? null : "Unexpected bytes after the end of the request";
            }
            catch (Eth2ReqRespException e)
            {
                return e.Message;
            }
            catch (EndOfStreamException e)
            {
                return $"Truncated request: {e.Message}";
            }
        }
    }

    /// <summary>The snappy frames after the zero length prefix of an empty request; any legal framing that decodes to no bytes is accepted.</summary>
    /// <remarks>An empty payload has no data frame to end it, so its framing runs to the requester's half-close, to max_compressed_len(0) or to the first data frame.</remarks>
    internal sealed class EmptyRequestFraming
    {
        private readonly MemoryStream _frames = new();
        private readonly byte[] _header = new byte[4];
        private int _framedBytesRead;
        private bool _sawDataFrame;

        // phase0 p2p ssz_snappy: nothing past max_compressed_len(0) is read, so framing that fills the bound ends there.
        internal bool IsComplete => _sawDataFrame || _framedBytesRead >= MaxEmptyRequestFramingBytes;

        /// <summary>Whether the data frame ended the request short of the bound, so a further byte is invalid.</summary>
        internal bool ExpectsNoMoreBytes => _sawDataFrame && _framedBytesRead < MaxEmptyRequestFramingBytes;

        /// <summary>Reads the rest of the frame whose first byte, its type, was already read.</summary>
        internal async Task ReadFrameAsync(Stream stream, byte frameType, CancellationToken token)
        {
            _header[0] = frameType;
            if (_framedBytesRead + _header.Length > MaxEmptyRequestFramingBytes)
            {
                throw new Eth2ReqRespException($"Snappy framing of an empty request exceeds the {MaxEmptyRequestFramingBytes}-byte bound");
            }

            await stream.ReadExactlyAsync(_header.AsMemory(1), token);
            int dataLength = _header[1] | (_header[2] << 8) | (_header[3] << 16);
            _framedBytesRead += 4 + dataLength;
            if (_framedBytesRead > MaxEmptyRequestFramingBytes)
            {
                throw new Eth2ReqRespException($"Snappy framing of an empty request exceeds the {MaxEmptyRequestFramingBytes}-byte bound");
            }

            byte[] data = new byte[dataLength];
            await stream.ReadExactlyAsync(data, token);
            if (_frames.Length == 0 && frameType != StreamIdentifierFrame)
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
                    _sawDataFrame = true;
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
                    return;
                default:
                    throw new Eth2ReqRespException($"Unskippable reserved snappy frame type 0x{frameType:x2}");
            }

            _frames.Write(_header);
            _frames.Write(data);
        }

        /// <summary>Checks that the frames read so far decode to no bytes.</summary>
        internal void Validate()
        {
            _frames.Position = 0;
            try
            {
                using SnappyStream snappy = new(_frames, CompressionMode.Decompress, leaveOpen: true);
                if (snappy.Read(new byte[1]) != 0)
                {
                    throw new Eth2ReqRespException("Snappy frames of an empty request decode to data");
                }
            }
            catch (Exception e) when (e is not Eth2ReqRespException)
            {
                throw new Eth2ReqRespException($"Snappy decompression failed: {e.Message}");
            }
            finally
            {
                _frames.Position = _frames.Length;
            }
        }
    }

    // The frames already in are read and the request is complete where they stop; the tail takes what is still to come.
    private static async Task<RequestTail> ReadEmptyRequestFramingAsync(Stream stream, CancellationToken token)
    {
        EmptyRequestFraming framing = new();
        byte[] frameType = new byte[1];
        while (!framing.IsComplete && await TryReadBufferedByteAsync(stream, frameType, token))
        {
            await framing.ReadFrameAsync(stream, frameType[0], token);
        }

        framing.Validate();
        if (framing.ExpectsNoMoreBytes)
        {
            await RejectTrailingBytesAsync(stream, token);
        }

        return framing.IsComplete && !framing.ExpectsNoMoreBytes ? RequestTail.Closed : new RequestTail(framing);
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

    private static async Task<(byte[] Payload, bool TailOpen)> ReadFramedPayloadAsync(Stream stream, ulong declaredLength, int maxSize, CancellationToken token, bool isRequest = false)
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

        // The reader must not read past max_compressed_len(n), so a payload that used the whole bound is not probed or watched.
        bool tailOpen = isRequest && framedBytesRead < maxFramedBytes;
        if (tailOpen)
        {
            await RejectTrailingBytesAsync(stream, token);
        }

        return (payload, tailOpen);
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
            if (i == 9 && buffer[0] > 0x01)
            {
                throw new Eth2ReqRespException("Varint length header overflows Uint64");
            }

            value |= (ulong)(buffer[0] & 0x7f) << (7 * i);
            if ((buffer[0] & 0x80) == 0)
            {
                return value;
            }
        }

        throw new Eth2ReqRespException("Varint length header longer than 10 bytes");
    }
}
