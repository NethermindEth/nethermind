using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using DotNetty.Transport.Channels;
using Nethermind.Core;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P
{
    public class Multiplexor : ChannelHandlerAdapter, IMessageSender
    {
        private readonly int _dataTransferWindow;
        private readonly ILogger _logger;

        private readonly ConcurrentDictionary<int, ProtocolQueues> _protocolQueues = new ConcurrentDictionary<int, ProtocolQueues>();
        private readonly IMessageSerializationService _serializationService;

        private readonly ConcurrentDictionary<int, int> _windowSizes = new ConcurrentDictionary<int, int>();
        private IChannelHandlerContext _context;

        public Multiplexor(IMessageSerializationService serializationService, ILogger logger, int dataTransferWindow = 1024 * 8)
        {
            _serializationService = serializationService;
            _logger = logger;
            _dataTransferWindow = dataTransferWindow;
            WindowSizes = new ReadOnlyDictionary<int, int>(_windowSizes);
        }

        public ReadOnlyDictionary<int, int> WindowSizes { get; }

        public void Enqueue<T>(T message, bool priority = false) where T : P2PMessage
        {
            try
            {
                byte[] serialized = _serializationService.Serialize(message);
                Packet packet = new Packet(message.Protocol, message.PacketType, serialized); // TODO: should serialized serialize / deserialize between messages and packets
                Send(packet);
            }
            catch (Exception e)
            {
                _logger.Error($"Packet ({message.Protocol}.{message.PacketType}) pushed", e);
            }
        }

        private void Send(Packet packet)
        {
            // TODO: split packet, encode frames, assign to buffers for appripriate protocols
            // TODO: release in cycle from queues
            _context.WriteAndFlushAsync(packet).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger.Error($"{nameof(NettyP2PHandler)} exception", t.Exception);
                }
                else if (t.IsCompleted)
                {
                    _logger.Error($"Packet ({packet.ProtocolType}.{packet.PacketType}) pushed", t.Exception);
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