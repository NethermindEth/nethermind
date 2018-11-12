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
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using Nethermind.Blockchain;
using Nethermind.Blockchain.TransactionPools;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P
{
    public class P2PSession : IP2PSession
    {
        private readonly ILogManager _logManager;
        private readonly ILogger _logger;
        private readonly IMessageSerializationService _serializer;
        private readonly ISynchronizationManager _syncManager;
        private readonly INodeStatsProvider _nodeStatsProvider;
        private readonly IPerfService _perfService;
        private readonly IBlockTree _blockTree;
        private readonly ITransactionPool _transactionPool;
        private readonly ITimestamp _timestamp;
        private readonly Dictionary<string, IProtocolHandler> _protocols = new Dictionary<string, IProtocolHandler>();

        private readonly IChannel _channel;
        private IChannelHandlerContext _context;
        private IPacketSender _packetSender;

        private Func<int, (string, int)> _adaptiveCodeResolver;
        private Func<(string ProtocolCode, int PacketType), int> _adaptiveEncoder;
        private bool _wasDisconnected;
        private INodeStats _nodeStats;

        public P2PSession(NodeId localNodeId,
            NodeId remoteId,
            int localPort,
            ConnectionDirection connectionDirection,
            IMessageSerializationService serializer,
            ISynchronizationManager syncManager,
            INodeStatsProvider nodeStatsProvider,
            INodeStats nodeStats,
            ILogManager logManager,
            IChannel channel,
            IPerfService perfService,
            IBlockTree blockTree,
            ITransactionPool transactionPool,
            ITimestamp timestamp)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _nodeStatsProvider = nodeStatsProvider ?? throw new ArgumentNullException(nameof(nodeStatsProvider));
            _perfService = perfService ?? throw new ArgumentNullException(nameof(perfService));
            _blockTree = blockTree;
            _transactionPool = transactionPool;
            _timestamp = timestamp;
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _syncManager = syncManager ?? throw new ArgumentNullException(nameof(syncManager));
            _logger = logManager.GetClassLogger();
            
            NodeStats = nodeStats;
            LocalNodeId = localNodeId;
            RemoteNodeId = remoteId;
            LocalPort = localPort;
            SessionId = Guid.NewGuid().ToString();
            ConnectionDirection = connectionDirection;
        }

        public NodeId LocalNodeId { get; }
        public int LocalPort { get; }
        public NodeId RemoteNodeId { get; set; }
        public string RemoteHost { get; set; }
        public int? RemotePort { get; set; }
        public ConnectionDirection ConnectionDirection { get; set; }
        public string SessionId { get; }

        public INodeStats NodeStats
        {
            get
            {
                //It is needed for lazy creation of NodeStats, in case  IN connections, publicKey is available only after handshake
                if (_nodeStats == null)
                {
                    if (RemoteNodeId == null)
                    {
                        throw new Exception("Cannot get NodeStats without NodeId");
                    }

                    _nodeStats = _nodeStatsProvider.GetOrAddNodeStats(RemoteNodeId, RemoteHost, RemotePort ?? 0);
                }

                return _nodeStats;
            }
            set => _nodeStats = value;
        }

        // TODO: this should be one level up
        public void EnableSnappy()
        {
            if (_logger.IsTrace) _logger.Trace($"{RemoteNodeId} Enabling Snappy compression and disabling framing");
            _context.Channel.Pipeline.Get<NettyPacketSplitter>().DisableFraming();
            _context.Channel.Pipeline.AddBefore($"{nameof(Multiplexor)}#0", null, new SnappyDecoder(_logger));
            _context.Channel.Pipeline.AddBefore($"{nameof(Multiplexor)}#0", null, new SnappyEncoder(_logger));
        }

        public void DeliverMessage(Packet packet, bool priority = false)
        {
            if (_logger.IsTrace) _logger.Trace($"P2P to deliver {packet.Protocol}.{packet.PacketType} with payload {packet.Data.ToHexString()}");

            packet.PacketType = _adaptiveEncoder((packet.Protocol, packet.PacketType));
            _packetSender.Enqueue(packet, priority);
        }

        public void ReceiveMessage(Packet packet)
        {
            int dynamicMessageCode = packet.PacketType;
            (string protocol, int messageId) = ResolveMessageCode(dynamicMessageCode);
            packet.Protocol = protocol;

            if (_logger.IsTrace) _logger.Trace($"{RemoteNodeId} {nameof(P2PSession)} received a message of length {packet.Data.Length} ({dynamicMessageCode} => {protocol}.{messageId})");

            if (protocol == null)
            {
                if (_logger.IsTrace) _logger.Warn($"Received a message from node: {RemoteNodeId}, ({dynamicMessageCode} => {messageId}), known protocols ({_protocols.Count}): {string.Join(", ", _protocols.Select(x => $"{x.Key} {x.Value.ProtocolVersion} {x.Value.MessageIdSpaceSize}"))}");
                return;
            }

            packet.PacketType = messageId;
            _protocols[protocol].HandleMessage(packet);
        }

        // TODO: use custom interface instead of netty one (can encapsulate both)
        public void Init(byte p2PVersion, IChannelHandlerContext context, IPacketSender packetSender)
        {
            _packetSender = packetSender;
            _context = context;
            InitProtocol(Protocol.P2P, p2PVersion);
        }

        public void Handshake()
        {
            HandshakeComplete?.Invoke(this, EventArgs.Empty);
        }

        public async Task InitiateDisconnectAsync(DisconnectReason disconnectReason)
        {
            if (_wasDisconnected)
            {
                if (_logger.IsTrace) _logger.Trace($"Session was already disconnected: {RemoteNodeId}, sessioId: {SessionId}");
                return;
            }

            //Trigger disconnect on each protocol handler (if p2p is initialized it will send disconnect message to the peer)
            if (_protocols.Any())
            {
                foreach (var protocolHandler in _protocols.Values)
                {
                    protocolHandler.Disconnect(disconnectReason);
                }
            }

            await DisconnectAsync(disconnectReason, DisconnectType.Local);
        }

        public async Task DisconnectAsync(DisconnectReason disconnectReason, DisconnectType disconnectType)
        {
            if (_wasDisconnected)
            {
                return;
            }

            _wasDisconnected = true;
            if (PeerDisconnected != null)
            {
                PeerDisconnected.Invoke(this, new DisconnectEventArgs(disconnectReason, disconnectType));
            }
            else if (_logger.IsDebug) _logger.Debug("No subscriptions for PeerDisconnected");

            //Possible in case of disconnect before p2p initialization
            if (_context == null)
            {
                //in case pipeline did not get to p2p - no disconnect delay
                await _channel.DisconnectAsync();
                return;
            }
            await Task.Delay(Timeouts.Disconnection).ContinueWith(t =>
            {
                _context.DisconnectAsync();
                if (_logger.IsTrace) _logger.Trace($"{RemoteNodeId} Disconnecting now after {Timeouts.Disconnection.TotalMilliseconds} milliseconds");
            });
        }

        public event EventHandler<DisconnectEventArgs> PeerDisconnected;
        public event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;
        public event EventHandler<EventArgs> HandshakeComplete;

        private (string, int) ResolveMessageCode(int adaptiveId)
        {
            return _adaptiveCodeResolver.Invoke(adaptiveId);
        }

        private void InitProtocol(string protocolCode, int version)
        {
            protocolCode = protocolCode.ToLowerInvariant();
            if (_protocols.ContainsKey(protocolCode))
            {
                throw new InvalidOperationException($"{RemoteNodeId} Session for protocol {protocolCode} already started");
            }

            if (protocolCode != Protocol.P2P && !_protocols.ContainsKey(Protocol.P2P))
            {
                throw new InvalidOperationException($"{Protocol.P2P} protocolHandler has to be started before starting {protocolCode} protocolHandler");
            }

            IProtocolHandler protocolHandler;
            switch (protocolCode)
            {
                case Protocol.P2P:
                    protocolHandler = new P2PProtocolHandler(this, _serializer, LocalNodeId, LocalPort, _logManager, _perfService);
                    protocolHandler.ProtocolInitialized += (sender, args) =>
                    {
                        if (protocolHandler.ProtocolVersion >= 5)
                        {
                            if (_logger.IsTrace) _logger.Trace($"{RemoteNodeId} {protocolHandler.ProtocolCode} v{protocolHandler.ProtocolVersion} established - Enabling Snappy");
                            EnableSnappy();
                        }
                        else
                        {
                            if (_logger.IsTrace) _logger.Trace($"{RemoteNodeId} {protocolHandler.ProtocolCode} v{protocolHandler.ProtocolVersion} established - Disabling Snappy");
                        }

                        ProtocolInitialized?.Invoke(this, args);
                    };
                    break;
                case Protocol.Eth:
                    if (version < 62 || version > 63)
                    {
                        throw new NotSupportedException($"Eth protocolo version {version} is not supported.");
                    }

                    protocolHandler = version == 62
                        ? new Eth62ProtocolHandler(this, _serializer, _syncManager, _logManager, _perfService, _blockTree, _transactionPool, _timestamp)
                        : new Eth63ProtocolHandler(this, _serializer, _syncManager, _logManager, _perfService, _blockTree, _transactionPool, _timestamp);
                    protocolHandler.ProtocolInitialized += (sender, args) =>
                    {
                        ProtocolInitialized?.Invoke(this, args);
                    };
                    break;
                default:
                    throw new NotSupportedException();
            }

            protocolHandler.SubprotocolRequested += (sender, args) => InitProtocol(args.ProtocolCode, args.Version);
            _protocols[protocolCode] = protocolHandler;

            (string ProtocolCode, int SpaceSize)[] alphabetically = new (string, int)[_protocols.Count];
            alphabetically[0] = (Protocol.P2P, _protocols[Protocol.P2P].MessageIdSpaceSize);
            int i = 1;
            foreach (KeyValuePair<string, IProtocolHandler> protocolSession in _protocols.Where(kv => kv.Key != "p2p")
                .OrderBy(kv => kv.Key))
            {
                alphabetically[i++] = (protocolSession.Key, protocolSession.Value.MessageIdSpaceSize);
            }

            _adaptiveCodeResolver = dynamicId =>
            {
                int offset = 0;
                for (int j = 0; j < alphabetically.Length; j++)
                {
                    if (offset + alphabetically[j].SpaceSize > dynamicId)
                    {
                        return (alphabetically[j].ProtocolCode, dynamicId - offset);
                    }

                    offset += alphabetically[j].SpaceSize;
                }

                if(_logger.IsTrace) _logger.Warn($"Could not resolve message id from {dynamicId} with known: {string.Join(", ", alphabetically.Select(x => $"{x.ProtocolCode} {x.SpaceSize}"))}");

                return (null, 0);
            };

            _adaptiveEncoder = args =>
            {
                int offset = 0;
                for (int j = 0; j < alphabetically.Length; j++)
                {
                    if (alphabetically[j].ProtocolCode == args.ProtocolCode)
                    {
                        return offset + args.PacketType;
                    }

                    offset += alphabetically[j].SpaceSize;
                }

                return args.PacketType;
            };

            protocolHandler.Init();
        }
    }
}