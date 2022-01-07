using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.SnapSync
{
    public class SnapProvider
    {
        //private readonly StateTree _tree;
        private readonly TrieStore _store;
        private SortedSet<Keccak> _sortedAddressHashes = new();

        public SnapProvider(StateTree tree, TrieStore store)
        {
            //_tree = tree;
            _store = store;
        }

        public Keccak? AddAccountRange(long blockNumber, Keccak expectedRootHash, Keccak startingHash, AccountWithAddressHash[] accounts, byte[][] proofs)
        {
            // TODO: Check the accounts boundaries and sorting

            (bool proved, _) = ProofVerifier.VerifyMultipleProofs(proofs, expectedRootHash);

            if (!proved)
            {
                //TODO: log incorrect proofs
                return null;
            }

            // leftProof, rightProof
            // toNibble both proofs
            // create path when traversing the tree and compare bytes by indices

            StateTree tree = new StateTree(_store, LimboLogs.Instance);
            FillBoundaryTree(tree, expectedRootHash, proofs, startingHash, accounts.Last().AddressHash);


            foreach (var account in accounts)
            {
                tree.Set(account.AddressHash, account.Account);
            }

            tree.Commit(blockNumber);

            return tree.RootHash;
        }

        private void FillBoundaryTree(StateTree tree, Keccak expectedRootHash, byte[][] proofs, Keccak startingHash, Keccak endHash)
        {
            if(tree == null)
            {
                throw new ArgumentNullException(nameof(tree));
            }

            Dictionary<Keccak, TrieNode> processed = new();
            TrieNode parent = null;
            int nibbleIndex = -1;
            List<byte> path = new();
            Span<byte> leftNibbles = stackalloc byte[64];
            Nibbles.BytesToNibbleBytes(startingHash.Bytes, leftNibbles);
            Span<byte> rightNibbles = stackalloc byte[64];
            Nibbles.BytesToNibbleBytes(endHash.Bytes, rightNibbles);

            for (int i = 0; i < proofs.Length; i++)
            {
                byte[] proof = proofs[i];

                var node = new TrieNode(NodeType.Unknown, proof, true);
                node.ResolveNode(_store);
                node.ResolveKey(_store, i == 0);
                Keccak currentKeccak = node.Keccak;

                if (processed.TryGetValue(currentKeccak, out TrieNode processedNode))
                {
                    node = processedNode;
                }
                else
                {
                    //node.ResolveNode(_store);
                    processed.Add(currentKeccak, node);
                }

                if (currentKeccak == expectedRootHash)
                {
                    if (tree.RootRef is null)
                    {
                        tree.RootRef = node;
                    }
                }
                else
                {
                    TrieNode newChild = node.IsLeaf ? null : node;

                    //parent.IsDirty = true;

                    if (parent.IsExtension)
                    {
                        parent.SetChild(0, newChild);
                    }
                    if (parent.IsBranch)
                    {
                        int left = Bytes.Comparer.Compare(path.ToArray(), leftNibbles[0..path.Count()].ToArray()) == 0 ? leftNibbles[nibbleIndex] : -1;
                        int right = Bytes.Comparer.Compare(path.ToArray(), rightNibbles[0..path.Count()].ToArray()) == 0 ? rightNibbles[nibbleIndex] : 16;

                        for (byte ci = 0; ci < 16; ci++)
                        {
                            Keccak? childKeccak = parent.GetChildHash(ci);

                            if (ci > left && ci < right)
                            {
                                parent.SetChild(ci, null);
                            }

                            if (childKeccak != null && childKeccak == currentKeccak)
                            {
                                parent.SetChild(ci, newChild);
                                path.Add(ci);
                            }
                        }
                    }
                }

                parent = node;
               

                if (currentKeccak == expectedRootHash)
                {
                    nibbleIndex = -1;
                    path = new();
                }

                if (node.IsExtension)
                {
                    nibbleIndex += node.Path.Length;
                    path.AddRange(parent.Path);
                }
                else if (node.IsBranch)
                {
                    nibbleIndex++;
                }
            }
        }
    }
}
