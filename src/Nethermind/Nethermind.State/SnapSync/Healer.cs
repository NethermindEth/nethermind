using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.SnapSync
{
    internal class Healer
    {
        private readonly IDb _db;
        private readonly ITrieStore _store;
        private HashSet<Keccak> _missingNodes = new ();

        public Healer(IDb db, ITrieStore store)
        {
            _db = db;
            _store = store;
        }

        public void ProcessNode(byte[] nodeBytes)
        {
            if (nodeBytes == null)
            {
                throw new ArgumentNullException(nameof(nodeBytes));
            }

            TrieNode node = new TrieNode(NodeType.Unknown, nodeBytes);
            node.ResolveNode(NullTrieNodeResolver.Instance);

            if (_db.KeyExists(node.Keccak))
            {
                ProcessChildren(node);
            }
            else
            {
                _db.Set(node.Keccak, nodeBytes);
            }

            _missingNodes.Remove(node.Keccak);
        }

        public Keccak[] GetMissingNodes()
        {
            return _missingNodes.ToArray();
        }

        private void ProcessChildren(TrieNode node)
        {
            if (node.IsLeaf)
            {
                return;
            }

            if (node.IsExtension)
            {
                ProcessOneChild(node, 0);
            }
            else if (node.IsBranch)
            {
                for (int i = 0; i <= 15; i++)
                {
                    ProcessOneChild(node, i);
                }
            }

            void ProcessOneChild(TrieNode node, int childIndex)
            {
                Keccak? childHash = node.GetChildHash(childIndex);
                if (childHash != null)
                {
                    byte[]? childBytes = _db.Get(childHash);
                    if (childBytes != null)
                    {
                        ProcessNode(childBytes);
                    }
                    else
                    {
                        _missingNodes.Add(childHash);
                    }
                }
            }
        }
    }
}
