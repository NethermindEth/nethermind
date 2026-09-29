// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Common.Utilities;
using DotNetty.Handlers.Timeout;
using DotNetty.Transport.Channels;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;
using Snappier;

namespace Nethermind.Network.P2P.ProtocolHandlers;

public class ZeroNettyP2PHandler(ISession session, ILogManager logManager) : SimpleChannelInboundHandler<ZeroPacket>
{
    private const int MaxRetainedOutputCapacity = 64 * 1024;
    private ZeroPacket? _outputPacket;
    private bool _stopped;
    private readonly ISession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly ILogger _logger = logManager?.GetClassLogger<ZeroNettyP2PHandler>() ?? throw new ArgumentNullException(nameof(logManager));
    private readonly SnappyOutputWriter _snappyOutputWriter = new();

    public bool SnappyEnabled { get; private set; }

    public void Init(IPacketSender packetSender, IChannelHandlerContext context) => _session.Init(5, context, packetSender);

    public override void ChannelRegistered(IChannelHandlerContext context)
    {
        if (_logger.IsDebug) _logger.Debug($"Registering {nameof(ZeroNettyP2PHandler)}");
        base.ChannelRegistered(context);
    }

    protected override void ChannelRead0(IChannelHandlerContext ctx, ZeroPacket input)
    {
        IByteBuffer content = input.Content;
        int readableBytes = content.ReadableBytes;
        if (readableBytes > SnappyParameters.MaxSnappyLength)
        {
            _session.InitiateDisconnect(DisconnectReason.BreachOfProtocol, "Max message size exceeded");
            return;
        }
        if (SnappyEnabled)
        {
            ReadOnlySpan<byte> snappyInput = content.Array.AsSpan(content.ArrayOffset + content.ReaderIndex, readableBytes);
            int uncompressedLength;
            try
            {
                uncompressedLength = Snappy.GetUncompressedLength(snappyInput);
            }
            catch (InvalidDataException exception)
            {
                LogSnappyDecompressionFailure(_logger, content, readableBytes);
                throw new CorruptedFrameException(exception);
            }

            if ((uint)uncompressedLength > (uint)SnappyParameters.MaxSnappyLength)
            {
                _session.InitiateDisconnect(DisconnectReason.BreachOfProtocol, "Max message size exceeded");
                return;
            }

            if (!SnappyBlockValidator.IsValid(snappyInput, uncompressedLength))
            {
                LogSnappyDecompressionFailure(_logger, content, readableBytes);
                throw new CorruptedFrameException("Invalid Snappy block");
            }

            if (readableBytes > SnappyParameters.MaxSnappyLength / 4)
            {
                if (_logger.IsTrace) _logger.Trace($"Big Snappy message of length {readableBytes}");
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Uncompressing with Snappy a message of length {readableBytes}");
            }

            ZeroPacket outputPacket = TakeOutputPacket(ctx, uncompressedLength);
            IByteBuffer output = outputPacket.Content;

            try
            {
                _snappyOutputWriter.Buffer = output;
                // The writer overload decompresses directly into our buffer, avoiding Snappier's temporary rental and copy.
                Snappy.Decompress(new ReadOnlySequence<byte>(content.Array.AsMemory(content.ArrayOffset + content.ReaderIndex, readableBytes)),
                    _snappyOutputWriter);
                Debug.Assert(output.ReadableBytes == uncompressedLength, "Validated Snappy output must match its declared length.");
            }
            catch (InvalidDataException exception)
            {
                output.SafeRelease();
                LogSnappyDecompressionFailure(_logger, content, readableBytes);
                throw new CorruptedFrameException(exception);
            }
            catch (Exception)
            {
                output.SafeRelease();
                throw;
            }
            finally
            {
                _snappyOutputWriter.Buffer = null;
            }

            content.SkipBytes(readableBytes);
            try
            {
                outputPacket.Protocol = null;
                outputPacket.PacketType = input.PacketType;
                _session.ReceiveMessage(outputPacket);
            }
            finally
            {
                // A retained downstream reference prevents reuse, including after a consumer throws.
                // Consumers must retain the buffer for aliases that escape ReceiveMessage. Span-backed RLP
                // readers copy transaction pre-hash bytes, so those transactions do not alias this buffer.
                if (!_stopped && _outputPacket is null && output.ReferenceCount == 1 && output.Capacity <= MaxRetainedOutputCapacity)
                    _outputPacket = outputPacket;
                else
                    outputPacket.SafeRelease();
            }
        }
        else
        {
            _session.ReceiveMessage(input);
        }
    }

    private ZeroPacket TakeOutputPacket(IChannelHandlerContext context, int length)
    {
        ZeroPacket? packet = _outputPacket;
        if (packet is not null && length <= MaxRetainedOutputCapacity)
        {
            _outputPacket = null;
            IByteBuffer buffer = packet.Content;
            if (buffer.Capacity >= length)
            {
                buffer.Clear().MarkReaderIndex().MarkWriterIndex();
                return packet;
            }
            packet.SafeRelease();
        }
        return new ZeroPacket(context.Allocator.Buffer(length));
    }

    public override void ChannelInactive(IChannelHandlerContext context)
    {
        ReleaseOutputPacket();
        base.ChannelInactive(context);
    }

    public override void HandlerRemoved(IChannelHandlerContext context)
    {
        ReleaseOutputPacket();
        base.HandlerRemoved(context);
    }

    private void ReleaseOutputPacket()
    {
        _stopped = true;
        ZeroPacket? packet = _outputPacket;
        _outputPacket = null;
        packet?.SafeRelease();
    }

    public override void ExceptionCaught(IChannelHandlerContext context, Exception exception)
    {
        if (exception is SocketException or RlpException or ReadTimeoutException)
        {
            if (_logger.IsTrace) TraceCommunicationError(exception);
        }
        else
        {
            if (_logger.IsDebug) _logger.Debug($"Error in communication with {GetClientId(_session)}: {exception}");
        }

        if (exception is IInternalNethermindException)
        {
            // Do nothing as we don't want to drop peer for internal issue.
        }
        else if (_session?.Node?.IsStatic != true && _session?.Node?.IsTrusted != true)
        {
            DisconnectReason reason =
                exception is SocketException { SocketErrorCode: SocketError.ConnectionReset }
                    ? DisconnectReason.ConnectionReset
                    : DisconnectReason.Exception;
            _session.InitiateDisconnect(reason, $"Error in communication with {GetClientId(_session)} ({exception.GetType().Name}): {exception.Message}");
        }
        else
        {
            base.ExceptionCaught(context, exception);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceCommunicationError(Exception communicationException) =>
            _logger.Trace($"Error in communication with {GetClientId(_session)} ({communicationException.GetType().Name}): {communicationException}");
    }

    private static string GetClientId(ISession? session) =>
        session?.Node?.ToString(Node.Format.Console) ?? $"unknown {session?.RemoteHost}";

    private static void LogSnappyDecompressionFailure(ILogger logger, IByteBuffer content, int readableBytes)
    {
        if (logger.IsDebug)
        {
            ReadOnlyMemory<byte> prefix = content.Array.AsMemory(content.ArrayOffset + content.ReaderIndex, Math.Min(32, readableBytes));
            logger.Debug($"Snappy decompression failed for {readableBytes} bytes: {prefix.ToHexString()}");
        }
    }

    public void EnableSnappy() => SnappyEnabled = true;

    private sealed class SnappyOutputWriter : IBufferWriter<byte>
    {
        public IByteBuffer? Buffer { get; set; }

        public void Advance(int count) => Buffer!.SetWriterIndex(Buffer.WriterIndex + count);

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            IByteBuffer buffer = Buffer!;
            buffer.EnsureWritable(Math.Max(1, sizeHint));
            return buffer.Array.AsMemory(buffer.ArrayOffset + buffer.WriterIndex, buffer.WritableBytes);
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }
}
