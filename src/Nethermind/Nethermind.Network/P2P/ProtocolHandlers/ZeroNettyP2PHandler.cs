// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net.Sockets;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using DotNetty.Transport.Channels;
using Nethermind.Core.Exceptions;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Snappy;

namespace Nethermind.Network.P2P.ProtocolHandlers
{
    public class ZeroNettyP2PHandler : SimpleChannelInboundHandler<ZeroPacket>
    {
        private readonly ISession _session;
        private readonly ILogger _logger;

        public bool SnappyEnabled { get; private set; }

        public ZeroNettyP2PHandler(ISession session, ILogManager logManager)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _logger = logManager?.GetClassLogger<ZeroNettyP2PHandler>() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public void Init(IPacketSender packetSender, IChannelHandlerContext context)
        {
            _session.Init(5, context, packetSender);
        }

        public override void ChannelRegistered(IChannelHandlerContext context)
        {
            if (_logger.IsDebug) _logger.Debug($"Registering {nameof(ZeroNettyP2PHandler)}");
            base.ChannelRegistered(context);
        }

        protected override void ChannelRead0(IChannelHandlerContext ctx, ZeroPacket input)
        {
            IByteBuffer content = input.Content;
            if (SnappyEnabled)
            {
                int uncompressedLength = SnappyCodec.GetUncompressedLength(content.Array, content.ArrayOffset + content.ReaderIndex, content.ReadableBytes);
                if (uncompressedLength > SnappyParameters.MaxSnappyLength)
                {
                    throw new Exception("Max message size exceeeded"); // TODO: disconnect here
                }

                if (content.ReadableBytes > SnappyParameters.MaxSnappyLength / 4)
                {
                    if (_logger.IsTrace) _logger.Trace($"Big Snappy message of length {content.ReadableBytes}");
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"Uncompressing with Snappy a message of length {content.ReadableBytes}");
                }


                IByteBuffer output = ctx.Allocator.Buffer(uncompressedLength);

                try
                {
                    int length = SnappyCodec.Uncompress(content.Array, content.ArrayOffset + content.ReaderIndex, content.ReadableBytes, output.Array, output.ArrayOffset);
                    output.SetWriterIndex(output.WriterIndex + length);
                }
                catch (Exception)
                {
                    if (content.ReadableBytes == 2 && content.ReadByte() == 193)
                    {
                        // this is a Parity disconnect sent as a non-snappy-encoded message
                        // e.g. 0xc103
                    }
                    else
                    {
                        content.SkipBytes(content.ReadableBytes);
                        output.SafeRelease();
                        throw;
                    }
                }

                content.SkipBytes(content.ReadableBytes);
                ZeroPacket outputPacket = new(output);
                try
                {
                    outputPacket.PacketType = input.PacketType;
                    _session.ReceiveMessage(outputPacket);
                }
                finally
                {
                    outputPacket.SafeRelease();
                }
            }
            else
            {
                _session.ReceiveMessage(input);
            }
        }

        public override void ExceptionCaught(IChannelHandlerContext context, Exception exception)
        {
            //In case of SocketException we log it as debug to avoid noise
            string clientId = _session?.Node?.ToString("c") ?? $"unknown {_session?.RemoteHost}";
            if (exception is SocketException)
            {
                if (_logger.IsTrace) _logger.Trace($"Error in communication with {clientId} (SocketException): {exception}");
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Error in communication with {clientId}: {exception}");
            }

            if (exception is IInternalNethermindException)
            {
                // Do nothing as we don't want to drop peer for internal issue.
            }
            else if (_session?.Node?.IsStatic != true)
            {
                context.DisconnectAsync().ContinueWith(x =>
                {
                    if (x.IsFaulted && _logger.IsTrace)
                        _logger.Trace($"Error while disconnecting on context on {this} : {x.Exception}");
                });
            }
            else
            {
                base.ExceptionCaught(context, exception);
            }
        }

        public void EnableSnappy()
        {
            SnappyEnabled = true;
        }
    }
}
