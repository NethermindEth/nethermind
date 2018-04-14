using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;

namespace Nethermind.Network.P2P
{
    public class P2PManager : IP2PManager, IDiscoveryListener
    {
        private readonly ILogger _logger;
        private readonly IDictionary<string, RlpxPeer> _activePeers = new Dictionary<string, RlpxPeer>();
        private readonly IEncryptionHandshakeService _encryptionHandshakeService;
        private readonly IMessageSerializationService _messageSerializationService;
        private readonly ISynchronizationManager _synchronizationManager;

        public P2PManager(IEncryptionHandshakeService encryptionHandshakeService, ILogger logger, IMessageSerializationService messageSerializationService, ISynchronizationManager synchronizationManager)
        {
            _encryptionHandshakeService = encryptionHandshakeService;
            _logger = logger;
            _messageSerializationService = messageSerializationService;
            _synchronizationManager = synchronizationManager;
        }

        public void OnNodeDiscovered(DiscoveryNode node)
        {
            try
            {
                //TODO implement proper p2p connection initialization with node

                //var nodeId = node.PublicKey.ToString();
                //if (_activePeers.ContainsKey(nodeId))
                //{
                //    _logger.Log($"Peer already initialied: {nodeId}");
                //    return;
                //}

                //var peer = new RlpxPeer(node.PublicKey, node.Port, _encryptionHandshakeService, _messageSerializationService, _synchronizationManager, _logger);
                //_activePeers.Add(nodeId, peer);

                //Task.Run(() => InitializePeer(peer));
            }
            catch (Exception e)
            {
                _logger.Error($"Error during peer discovery: {node.PublicKey}", e);
            }
        }

        private async void InitializePeer(RlpxPeer peer)
        {
            try
            {
                await peer.Init();
            }
            catch (Exception e)
            {
                _logger.Error($"Error during peer initialization: {peer.LocalNodeId}", e);
            }
        }

        public IReadOnlyCollection<RlpxPeer> ActivePeers => _activePeers.Values.ToArray();
    }
}