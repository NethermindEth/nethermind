using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Peers;
using Nethermind.DataMarketplace.Providers.Repositories;

namespace Nethermind.DataMarketplace.Providers.Services
{
    internal class SessionManager : ISessionManager
    {
        private readonly IProviderSessionRepository _sessionRepository;
        private readonly ITimestamper _timestamper;

        private static readonly ConcurrentDictionary<PublicKey, ConsumerNode> Nodes =
            new ConcurrentDictionary<PublicKey, ConsumerNode>();

        private static readonly ConcurrentDictionary<Keccak, ConcurrentDictionary<PublicKey, ConsumerNode>> DepositNodes
            = new ConcurrentDictionary<Keccak, ConcurrentDictionary<PublicKey, ConsumerNode>>();

        private readonly ILogger _logger;

        public SessionManager(IProviderSessionRepository sessionRepository, ITimestamper timestamper,
            ILogManager logManager)
        {
            _sessionRepository = sessionRepository;
            _timestamper = timestamper;
            _logger = logManager.GetClassLogger();
        }

        public int GetNodesCount(Keccak depositId) => GetConsumerNodes(depositId)?.Count() ?? 0;

        public ProviderSession? GetSession(Keccak depositId, INdmProviderPeer peer) =>
            GetConsumerNode(peer)?.GetSession(depositId);

        public IEnumerable<ConsumerNode> GetConsumerNodes(Keccak? depositId = null)
            => depositId is null
                ? Nodes.Values
                : DepositNodes.TryGetValue(depositId, out var nodes)
                    ? nodes.Values
                    : Enumerable.Empty<ConsumerNode>();
        
        public ConsumerNode? GetConsumerNode(INdmProviderPeer peer)
            => Nodes.TryGetValue(peer.NodeId, out ConsumerNode? node) ? node : null;

        public void SetSession(ProviderSession session, INdmProviderPeer peer)
        {
            if (_logger.IsInfo) _logger.Info($"Setting an active session: '{session.Id}' for node: '{peer.NodeId}', address: '{peer.ConsumerAddress}', deposit: '{session.DepositId}'.");
            
            var node = GetConsumerNode(peer);
            if (node is null)
            {
                if (_logger.IsInfo) _logger.Info($"Couldn't set an active session: '{session.Id}' for node: '{peer.NodeId}', deposit: '{session.DepositId}' - consumer node was not found.");

                return;
            }

            node.AddSession(session);
            var depositNodes = DepositNodes.AddOrUpdate(session.DepositId,
                _ => new ConcurrentDictionary<PublicKey, ConsumerNode>(),
                (_, nodes) => nodes);
            depositNodes.TryRemove(peer.NodeId, out _);
            depositNodes.TryAdd(peer.NodeId, node);

            if (_logger.IsInfo) _logger.Info($"Set an active session: '{session.Id}' for node: '{peer.NodeId}', deposit: '{session.DepositId}'.");
        }

        public void AddPeer(INdmProviderPeer peer)
        {
            if (Nodes.TryAdd(peer.NodeId, new ConsumerNode(peer)))
            {
                if (_logger.IsInfo) _logger.Info($"Added node: '{peer.NodeId}' for address: '{peer.ConsumerAddress}'.");
                
                return;
            }
            
            if (_logger.IsError) _logger.Error($"Node: '{peer.NodeId}' for address: '{peer.ConsumerAddress}' couldn't be added.");
        }

        public async Task FinishSessionsAsync(INdmProviderPeer peer, bool removePeer = true)
        {
            if (!Nodes.TryGetValue(peer.NodeId, out var node))
            {
                if (_logger.IsInfo) _logger.Info($"No sessions to be finished found for node: '{peer.NodeId}'.");
                
                return;
            }
            
            if (_logger.IsInfo) _logger.Info($"Finishing session(s) for node: '{peer.NodeId}'.");
            var timestamp = _timestamper.UnixTime.Seconds;
            foreach (var session in node.Sessions)
            {
                if (_logger.IsInfo) _logger.Info($"Finishing a session: '{session.Id}' for deposit: '{session.DepositId}', node: '{peer.NodeId}'.");
                session.Finish(SessionState.ConsumerDisconnected, timestamp);
                await _sessionRepository.UpdateAsync(session);
                node.RemoveSession(session.DepositId);
                if (removePeer)
                {
                    TryRemoveDepositNode(session.DepositId, peer);
                }
                if (_logger.IsInfo) _logger.Info($"Finished a session: '{session.Id}' for deposit: '{session.DepositId}', node: '{peer.NodeId}', timestamp: {timestamp}.");
            }

            if (!removePeer)
            {
                return;
            }
            
            TryRemovePeer(node, peer);
        }

        public async Task FinishSessionAsync(Keccak depositId, INdmProviderPeer peer, bool removePeer = true)
        {
            var session = GetSession(depositId, peer);
            if (session is null)
            {
                if (_logger.IsInfo) _logger.Info($"Session for deposit: '{depositId}', node: '{peer.NodeId}' was not found.");

                return;
            }
 
            if (_logger.IsInfo) _logger.Info($"Finishing a session for deposit: '{depositId}', node: '{peer.NodeId}'.");
            var timestamp = _timestamper.UnixTime.Seconds;
            session.Finish(SessionState.FinishedByConsumer, timestamp);
            await _sessionRepository.UpdateAsync(session);
            peer.SendSessionFinished(session);
            ConsumerNode? node = GetConsumerNode(peer);
            if (node == null)
            {
                throw new InvalidDataException($"COuld not find consumer node for peer {peer.NodeId} with consumer address {peer.ConsumerAddress}");
            }
            
            node.RemoveSession(session.DepositId);
            if (_logger.IsInfo) _logger.Info($"Finished a session: '{session.Id}' for deposit: '{session.DepositId}', node: '{peer.NodeId}', timestamp: {timestamp}.");
            if (!removePeer)
            {
                return;
            }
            
            TryRemoveDepositNode(session.DepositId, peer);
            TryRemovePeer(node, peer);
        }
        
        private void TryRemoveDepositNode(Keccak depositId, INdmProviderPeer peer)
        {
            if (!DepositNodes.TryGetValue(depositId, out var nodes))
            {
                return;
            }

            nodes.TryRemove(peer.NodeId, out _);
            if (nodes.Count == 0)
            {
                DepositNodes.TryRemove(depositId, out _);
            }
        }

        private void TryRemovePeer(ConsumerNode consumerNode, INdmProviderPeer peer)
        {
            if (consumerNode.HasSessions)
            {
                if (_logger.IsInfo) _logger.Info($"Connected node: '{peer.NodeId}' has active sessions and will not be removed.");
                
                return;
            }
            
            if (Nodes.TryRemove(peer.NodeId, out _))
            {
                return;
            }
            
            if (_logger.IsError) _logger.Error($"Node: '{peer.NodeId}' for address: '{peer.ProviderAddress}' couldn't be removed.");
        }
    }
}