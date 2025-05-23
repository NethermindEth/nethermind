// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Nethermind.Libp2p.Core;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Optimism.CL.P2P;

public class PayloadByNumberProtocol : ISessionProtocol<ulong, ExecutionPayloadV3?>
{
    private const int MaxResponseSizeBytes = 10000000;
    private readonly ulong _chainId;
    private readonly IPayloadDecoder _payloadDecoder;
    private readonly ILogger _logger;

    public string Id => $"/opstack/req/payload_by_number/{_chainId}/0";

    public PayloadByNumberProtocol(
        ulong chainId,
        IPayloadDecoder payloadDecoder,
        ILogManager logManager)
    {
        _chainId = chainId;
        _payloadDecoder = payloadDecoder;
        _logger = logManager.GetClassLogger();
    }

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        await downChannel.WriteAsync(new ReadOnlySequence<byte>([Result.Unavailable]));
    }

    private static class Result
    {
        public const byte Success = 0;
        public const byte Unavailable = 1;
        public const byte Invalid = 2;
    }

    private static class Version
    {
        public const uint Ecotone = 1;
        public const uint Isthmus = 2;
    }

    public async Task<ExecutionPayloadV3?> DialAsync(IChannel downChannel, ISessionContext context, ulong request)
    {
        byte[] requestBytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(requestBytes, request);
        await downChannel.WriteAsync(new ReadOnlySequence<byte>(requestBytes));
        var res = (await downChannel.ReadAsync(1)).Data.FirstSpan[0];
        switch (res)
        {
            case Result.Success:
                break;
            case Result.Unavailable:
                if (_logger.IsTrace) _logger.Trace($"{nameof(PayloadByNumberProtocol)}: Requested payload is unavailable. Payload number: {request}");
                return null;
            case Result.Invalid:
                if (_logger.IsWarn) _logger.Warn($"{nameof(PayloadByNumberProtocol)}: Got invalid request. Payload number: {request}");
                return null;
            default:
                if (_logger.IsWarn) _logger.Warn($"{nameof(PayloadByNumberProtocol)}: Got unknown error code({res}). Payload number: {request}");
                return null;
        }
        var version = BinaryPrimitives.ReadUInt32LittleEndian((await downChannel.ReadAsync(4)).Data.ToArray());

        if (version != Version.Ecotone)
        {
            if (_logger.IsWarn) _logger.Warn($"{nameof(PayloadByNumberProtocol)}: Unsupported version. Payload number: {request}");
            return null;
        }

        var decompressor = await ReadPayloadData(downChannel);

        if (decompressor is null)
        {
            return null;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(MaxResponseSizeBytes);
        try
        {
            var bytesRead = decompressor.Read(buffer, 0, buffer.Length);
            int totalRead = bytesRead;
            while (bytesRead > 0)
            {
                if (totalRead > MaxResponseSizeBytes)
                {
                    return null;
                }

                bytesRead = decompressor.Read(buffer, totalRead, buffer.Length - totalRead);
                totalRead += bytesRead;
            }

            return _payloadDecoder.DecodePayload(buffer.AsSpan(0, totalRead));
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"{nameof(PayloadByNumberProtocol)}: Exception during payload decoding {e}");
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<Stream?> ReadPayloadData(IReader reader)
    {
        List<ReadOnlySequenceStream> streams = new();
        long bytesRead = 0;
        var readResult = await reader.ReadAsync(MaxResponseSizeBytes, ReadBlockingMode.WaitAny);
        while (readResult.Result == IOResult.Ok)
        {
            bytesRead += readResult.Data.Length;
            if (bytesRead > MaxResponseSizeBytes)
            {
                return null;
            }
            streams.Add(new(readResult.Data));
            readResult = await reader.ReadAsync(MaxResponseSizeBytes, ReadBlockingMode.WaitAny);
        }

        if (readResult.Result != IOResult.Ended)
        {
            return null;
        }

        bytesRead += readResult.Data.Length;
        if (bytesRead > MaxResponseSizeBytes)
        {
            return null;
        }
        streams.Add(new(readResult.Data));

        return new Snappier.SnappyStream(new CombinedStream(streams.GetEnumerator()), CompressionMode.Decompress);
    }

    private class CombinedStream : Stream
    {
        private readonly IEnumerator<Stream> _streams;
        private Stream? _currentStream;

        public CombinedStream(IEnumerator<Stream> streams)
        {
            _streams = streams;
            MoveNext();
        }

        private void MoveNext() => _currentStream = _streams.MoveNext() ? _streams.Current : null;

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (_currentStream is not null)
            {
                int read = _currentStream.Read(buffer, offset, count);
                if (read > 0) return read;
                MoveNext();
            }

            return 0;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }

    private class ReadOnlySequenceStream(ReadOnlySequence<byte> sequence) : Stream
    {
        private SequencePosition _position = sequence.Start;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var reader = new SequenceReader<byte>(sequence.Slice(_position));

            int totalRead = 0;
            while (totalRead < count && reader.Remaining > 0)
            {
                var readableSpan = reader.CurrentSpan.Slice(reader.CurrentSpanIndex);
                int toCopy = Math.Min(count - totalRead, readableSpan.Length);
                readableSpan.Slice(0, toCopy).CopyTo(buffer.AsSpan(offset + totalRead));
                totalRead += toCopy;
                reader.Advance(toCopy);
            }
            _position = reader.Position;

            return totalRead;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => sequence.Length;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }
}
