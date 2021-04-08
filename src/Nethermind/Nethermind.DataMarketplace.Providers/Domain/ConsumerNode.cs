using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Providers.Peers;

namespace Nethermind.DataMarketplace.Providers.Domain
{
    public class ConsumerNode
    {
        private readonly ConcurrentDictionary<Keccak, ProviderSession> _sessions =
            new ConcurrentDictionary<Keccak, ProviderSession>();

        public INdmProviderPeer Peer { get; }
        public IEnumerable<ProviderSession> Sessions => _sessions.Values;
        public bool HasSessions => _sessions.Count > 0;

        public ConsumerNode(INdmProviderPeer peer)
        {
            Peer = peer;
        }

        public void AddSession(ProviderSession session) => _sessions.TryAdd(session.DepositId, session);
        public void RemoveSession(Keccak depositId) => _sessions.TryRemove(depositId, out _);

        public ProviderSession? GetSession(Keccak depositId) =>
            _sessions.TryGetValue(depositId, out var session) ? session : null;
    }
}