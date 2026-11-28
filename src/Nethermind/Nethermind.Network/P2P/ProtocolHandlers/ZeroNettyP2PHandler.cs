// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using DotNetty.Handlers.Timeout;
using DotNetty.Transport.Channels;
using Nethermind.Core.Exceptions;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.ProtocolHandlers;

public class ZeroNettyP2PHandler(ISession session, ILogManager logManager) : SimpleChannelInboundHandler<NettyBufferPacket>
{
    private const int MaxRetainedOutputCapacity = 64 * 1024;
    private NettyBufferPacket? _outputPacket;
    private bool _stopped;
    private readonly ISession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly ILogger _logger = logManager?.GetClassLogger<ZeroNettyP2PHandler>() ?? throw new ArgumentNullException(nameof(logManager));
    private readonly SnappyOutputWriter _snappyOutputWriter = new();

    public bool SnappyEnabled { get; private set; }

    public override void ChannelRegistered(IChannelHandlerContext context)
    {
        if (_logger.IsDebug) _logger.Debug($"Registering {nameof(ZeroNettyP2PHandler)}");
        base.ChannelRegistered(context);
    }

    protected override void ChannelRead0(IChannelHandlerContext ctx, NettyBufferPacket input)
    {
        IByteBuffer content = input.Buffer;
        int readableBytes = content.ReadableBytes;
        if (!SnappyDecoder.IsWithinSizeLimit(readableBytes) || (!SnappyEnabled && ExceedsProtocolLimit(input.PacketType, readableBytes)))
        {
            _session.InitiateDisconnect(DisconnectReason.BreachOfProtocol, "Max message size exceeded");
            return;
        }
        if (SnappyEnabled)
        {
            ReadOnlyMemory<byte> snappyInput = content.Array.AsMemory(content.ArrayOffset + content.ReaderIndex, readableBytes);
            int uncompressedLength;
            try
            {
                if (!SnappyDecoder.TryGetUncompressedLength(snappyInput, out uncompressedLength, _logger))
                {
                    _session.InitiateDisconnect(DisconnectReason.BreachOfProtocol, "Max message size exceeded");
                    return;
                }
            }
            catch (InvalidFrameException exception)
            {
                throw exception.ToCorruptedFrameException();
            }

            if (ExceedsProtocolLimit(input.PacketType, uncompressedLength))
            {
                _session.InitiateDisconnect(DisconnectReason.BreachOfProtocol, "Max message size exceeded");
                return;
            }

            if (readableBytes > SnappyParameters.MaxSnappyLength / 4)
            {
                if (_logger.IsTrace) _logger.Trace($"Big Snappy message of length {readableBytes}");
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Uncompressing with Snappy a message of length {readableBytes}");
            }

            NettyBufferPacket outputPacket = TakeOutputPacket(ctx, uncompressedLength);
            IByteBuffer output = outputPacket.Buffer;

            try
            {
                _snappyOutputWriter.Buffer = output;
                SnappyDecoder.Decompress(snappyInput, _snappyOutputWriter, _logger);
                Debug.Assert(output.ReadableBytes == uncompressedLength, "Validated Snappy output must match its declared length.");
            }
            catch (InvalidFrameException exception)
            {
                output.SafeRelease();
                throw exception.ToCorruptedFrameException();
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
                ReceiveMessage(ctx, outputPacket);
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
            ReceiveMessage(ctx, input);
        }
    }

    private void ReceiveMessage(IChannelHandlerContext context, ZeroPacket packet)
    {
        try
        {
            _session.ReceiveMessage(packet);
        }
        catch (RlpReaderException exception) when (exception is not IInternalNethermindException)
        {
            if (!_session.IsClosing)
            {
                ExceptionCaught(context, exception);
            }
        }
    }

    // eth/74 DependencyProofs is bounded by its advertised uncompressed size, before any decompression.
    private bool ExceedsProtocolLimit(int packetType, int uncompressedLength) =>
        _session is IMessageLimitSession limited && limited.ExceedsMessageLimit(packetType, uncompressedLength);

    private NettyBufferPacket TakeOutputPacket(IChannelHandlerContext context, int length)
    {
        NettyBufferPacket? packet = _outputPacket;
        if (packet is not null && length <= MaxRetainedOutputCapacity)
        {
            _outputPacket = null;
            IByteBuffer buffer = packet.Buffer;
            if (buffer.Capacity >= length)
            {
                buffer.Clear().MarkReaderIndex().MarkWriterIndex();
                return packet;
            }
            packet.SafeRelease();
        }
        return new NettyBufferPacket(context.Allocator.Buffer(length));
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
        NettyBufferPacket? packet = _outputPacket;
        _outputPacket = null;
        packet?.SafeRelease();
    }

    public override void ExceptionCaught(IChannelHandlerContext context, Exception exception)
    {
        if (exception is SocketException or RlpReaderException or ReadTimeoutException)
        {
            if (_logger.IsTrace) TraceCommunicationError(exception);
        }
        else
        {
            if (_logger.IsDebug) _logger.Debug($"Error in communication with {GetClientId(_session):hide}: {exception:hide}");
        }

        if (exception is IInternalNethermindException)
        {
            // Do nothing as we don't want to drop peer for internal issue.
        }
        else if (_session?.Node?.IsStatic != true && _session?.Node?.IsTrusted != true)
        {
            DisconnectReason reason = exception switch
            {
                SocketException { SocketErrorCode: SocketError.ConnectionReset } => DisconnectReason.ConnectionReset,
                RlpLimitException => DisconnectReason.MessageLimitsBreached,
                _ => DisconnectReason.Exception,
            };
            _session.InitiateDisconnect(reason, $"Error in communication with {GetClientId(_session)} ({exception.GetType().Name}): {exception.Message}");
        }
        else
        {
            base.ExceptionCaught(context, exception);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceCommunicationError(Exception communicationException) =>
            _logger.Trace($"Error in communication with {GetClientId(_session):hide} ({communicationException.GetType().Name}): {communicationException:hide}");
    }

    private static string GetClientId(ISession? session) =>
        session?.Node?.ToString(Node.Format.Console) ?? $"unknown {session?.RemoteHost}";

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
