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
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using DotNetty.Transport.Channels;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P
{
    // TODO: work in progress / lower priority
    public class Multiplexor : ChannelHandlerAdapter, IPacketSender
    {
        private readonly int _dataTransferWindow;
        private readonly ILogger _logger;

        private readonly ConcurrentDictionary<int, ProtocolQueues> _protocolQueues = new ConcurrentDictionary<int, ProtocolQueues>();

        private readonly ConcurrentDictionary<int, int> _windowSizes = new ConcurrentDictionary<int, int>();
        private IChannelHandlerContext _context;

        public Multiplexor(ILogManager logManager, int dataTransferWindow = 1024 * 8)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _dataTransferWindow = dataTransferWindow;
            WindowSizes = new ReadOnlyDictionary<int, int>(_windowSizes);
        }

        public ReadOnlyDictionary<int, int> WindowSizes { get; }

        public void Enqueue(Packet packet, bool priority = false)
        {
            try
            {
                Send(packet);
            }
            catch (Exception e)
            {
                _logger.Error($"Packet ({packet.Protocol}.{packet.PacketType}) failed", e);
            }
        }

        private void Send(Packet packet)
        {
            if (_context.Channel.Active)
            {
                return;
            }
         
            // TODO: split packet, encode frames, assign to buffers for appripriate protocols
            // TODO: release in cycle from queues
            _context.WriteAndFlushAsync(packet).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_context.Channel != null && !_context.Channel.Active)
                    {
                        if (_logger.IsDebugEnabled) _logger.Error($"{nameof(NettyP2PHandler)} error in multiplexor, channel is not active", t.Exception);
                    }
                    else if (_logger.IsErrorEnabled) _logger.Error($"{nameof(NettyP2PHandler)} error in multiplexor, channel is active", t.Exception);
                }
                else if (t.IsCompleted)
                {
                    if (_logger.IsTraceEnabled) _logger.Trace($"Packet ({packet.Protocol}.{packet.PacketType}) pushed");
                }
            });
        }

        public override void HandlerAdded(IChannelHandlerContext context)
        {
            _context = context;
        }

        public Multiplexor AddProtocol(int protocol, int initialWindowSize = 1024 * 8)
        {
            _windowSizes[protocol] = initialWindowSize;
            _protocolQueues[protocol] = new ProtocolQueues();
            return this;
        }

        private class ProtocolQueues
        {
            public ConcurrentQueue<Packet> PriorityQueue { get; set; } = new ConcurrentQueue<Packet>();
            public ConcurrentQueue<Packet> ChunkedQueue { get; set; } = new ConcurrentQueue<Packet>();
            public ConcurrentQueue<Packet> NonChunkedQueue { get; set; } = new ConcurrentQueue<Packet>();
        }
    }
}