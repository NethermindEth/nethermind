/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Net.Sockets;
using DotNetty.Transport.Channels;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Snappy;

namespace Nethermind.Network.P2P
{
    public class NettyP2PHandler : SimpleChannelInboundHandler<Packet>
    {
        private readonly ISession _session;
        private readonly ILogger _logger;

        public NettyP2PHandler(ISession session, ILogManager logManager)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public void Init(IPacketSender packetSender, IChannelHandlerContext context)
        {
            _session.Init(5, context, packetSender);
        }

        public override void ChannelRegistered(IChannelHandlerContext context)
        {
            if (_logger.IsDebug) _logger.Debug($"Registering {nameof(NettyP2PHandler)}");
            base.ChannelRegistered(context);
        }

        protected override void ChannelRead0(IChannelHandlerContext ctx, Packet msg)
        {
            if (SnappyEnabled)
            {
                if (SnappyCodec.GetUncompressedLength(msg.Data) > SnappyParameters.MaxSnappyLength)
                {
                    throw new Exception("Max message size exceeeded"); // TODO: disconnect here
                }

                if (msg.Data.Length > SnappyParameters.MaxSnappyLength / 4)
                {
                    if (_logger.IsWarn) _logger.Warn($"Big Snappy message of length {msg.Data.Length}");
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"Uncompressing with Snappy a message of length {msg.Data.Length}");
                }

                try
                {
                    msg.Data = SnappyCodec.Uncompress(msg.Data);
                }
                catch
                {
                    if (msg.Data.Length == 2 && msg.Data[0] == 193)
                    {
                        // this is a Parity disconnect sent as a non-snappy-encoded message
                        // e.g. 0xc103
                    }
                    else
                    {
                        throw;
                    }
                }
            }
            
            if (_logger.IsTrace) _logger.Trace($"Channel read... data length {msg.Data.Length}");
            _session.ReceiveMessage(msg);
        }

        public override void ExceptionCaught(IChannelHandlerContext context, Exception exception)
        {
            //In case of SocketException we log it as debug to avoid noise
            string clientId = _session?.Node.ClientId ?? $"unknown {_session?.RemoteHost}";
            if (exception is SocketException)
            {
                if (_logger.IsTrace) _logger.Trace($"Error in communication with {clientId} (SocketException): {exception}");
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Error in communication with {clientId}: {exception}");
            }

            context.DisconnectAsync();
        }

        public bool SnappyEnabled { get; private set; }
        
        public void EnableSnappy()
        {
            SnappyEnabled = true;
        }
    }
}