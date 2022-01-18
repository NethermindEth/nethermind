using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.SnapSync
{
    internal static class SnapProviderHelper
    {
        internal static void FillBoundaryTree(PatriciaTree tree, Keccak expectedRootHash, byte[][] proofs, Keccak startingHash, Keccak endHash)
        {
            if (tree == null)
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
                node.IsBoundaryProofNode = true;
                node.ResolveNode(tree.TrieStore);
                node.ResolveKey(tree.TrieStore, i == 0);
                Keccak currentKeccak = node.Keccak;

                if (processed.TryGetValue(currentKeccak, out TrieNode processedNode))
                {
                    node = processedNode;
                }
                else
                {
                    if (currentKeccak == expectedRootHash)
                    {
                        tree.RootRef = node;
                    }

                    processed.Add(currentKeccak, node);
                }

                if(parent != null)
                {
                    TrieNode newChild = node.IsLeaf ? null : node;

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
