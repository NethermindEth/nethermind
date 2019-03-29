using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Store;

namespace Nethermind.Blockchain.Synchronization
{
    public class NodeDataDownloader
    {
        private readonly ISnapshotableDb _db;
        private Keccak _root;

        private enum NodeType
        {
            Code,
            State
        }
            
        public NodeDataDownloader(ISnapshotableDb db)
        {
            _db = db;
        }
            
        private void SyncNodeData(Keccak root)
        {
            _root = root;
            _nodes.Add((root, NodeType.State));
        }

        private ConcurrentBag<(Keccak, NodeType)> _nodes = new ConcurrentBag<(Keccak, NodeType)>();

        private const int maxRequestSize = 256;
            
        public List<Keccak> PrepareRequest()
        {
            List<Keccak> request = new List<Keccak>();
            for (int i = 0; i < maxRequestSize; i++)
            {
                if (_nodes.TryTake(out (Keccak hash, NodeType nodeType) result))
                {
                    request.Add(result.hash);
                }
                else
                {
                    break;
                }
            }

            return request;
        }
    }
}