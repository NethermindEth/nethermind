using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Snap
{
    internal static class SnapProviderHelper
    {
        public static Keccak? AddAccountRange(StateTree tree, long blockNumber, Keccak expectedRootHash, Keccak startingHash, PathWithAccount[] accounts, byte[][] proofs = null)
        {
            // TODO: Check the accounts boundaries and sorting

            //var rlps = proofs.Select(p => $"{Keccak.Compute(p).ToString(false)}:{new Rlp(p).ToString(false)}").ToArray();
            //var res = string.Join($"{Environment.NewLine}{Environment.NewLine}", rlps);
            //var first = proofs.Select((p) => { var n = (new TrieNode(NodeType.Unknown, p, true)); n.ResolveNode(tree.TrieStore); return n; }) ;


            Keccak lastHash = accounts.Last().AddressHash;

            bool proved = ProcessProofs(tree, expectedRootHash, startingHash, lastHash, proofs);

            if (proved)
            {
                foreach (var account in accounts)
                {
                    tree.Set(account.AddressHash, account.Account);
                }

                tree.UpdateRootHash();

                if (tree.RootHash != expectedRootHash)
                {
                    // TODO: log incorrect range
                    return Keccak.EmptyTreeHash;
                }

                tree.Commit(blockNumber);
            }

            return tree.RootHash;
        }

        public static Keccak? AddStorageRange(StorageTree tree, long blockNumber, Keccak expectedRootHash, Keccak startingHash, SlotWithKeyHash[] slots, byte[][] proofs = null)
        {
            // TODO: Check the slots boundaries and sorting

            Keccak lastHash = slots.Last().KeyHash;

            bool proved = ProcessProofs(tree, expectedRootHash, startingHash, lastHash, proofs);

            if (proved)
            {
                foreach (var slot in slots)
                {
                    tree.Set(slot.KeyHash, slot.SlotValue);
                }

                tree.UpdateRootHash();

                if (tree.RootHash != expectedRootHash)
                {
                    // TODO: log incorrect range
                    return Keccak.EmptyTreeHash;
                }

                tree.Commit(blockNumber);
            }

            return tree.RootHash;
        }

        private static bool ProcessProofs(PatriciaTree tree, Keccak expectedRootHash, Keccak startingHash, Keccak lastHash, byte[][] proofs = null)
        {
            if (proofs != null && proofs.Length > 0)
            {
                //(bool proved, _) = ProofVerifier.VerifyMultipleProofs(proofs, expectedRootHash);

                //if (!proved)
                //{
                //    //TODO: log incorrect proofs
                //    return false;
                //}

                FillBoundaryTree_2(tree, expectedRootHash, proofs, startingHash, lastHash);
            }

            return true;
        }

        private static bool FillBoundaryTree_2(PatriciaTree tree, Keccak expectedRootHash, byte[][] proofs, Keccak startingHash, Keccak endHash)
        {
            if (proofs is null || proofs.Length == 0)
            {
                return true;
            }

            if (tree == null)
            {
                throw new ArgumentNullException(nameof(tree));
            }

            Dictionary<Keccak, TrieNode> dict = CreateProofDict(proofs, tree.TrieStore);

            Dictionary<Keccak, TrieNode> processed = new();
            Span<byte> leftBoundary = stackalloc byte[64];
            Nibbles.BytesToNibbleBytes(startingHash.Bytes, leftBoundary);
            Span<byte> rightBoundary = stackalloc byte[64];
            Nibbles.BytesToNibbleBytes(endHash.Bytes, rightBoundary);

            Stack<(TrieNode node, int pathIndex, List<byte> path)> proofNodesToProcess = new();

            if(dict.TryGetValue(expectedRootHash, out TrieNode root))
            {
                tree.RootRef = root;

                proofNodesToProcess.Push((root, -1, new List<byte>()));
            }
            else
            {
                return false;
            }

            while (proofNodesToProcess.Count > 0)
            {
                (TrieNode node, int pathIndex, List<byte> path) = proofNodesToProcess.Pop();

                if (node.IsExtension)
                {
                    Keccak? childKeccak = node.GetChildHash(0);
                    
                    if(childKeccak is not null && dict.TryGetValue(childKeccak, out TrieNode child))
                    {
                        node.SetChild(0, child);

                        pathIndex += node.Path.Length;
                        path.AddRange(node.Path);
                        proofNodesToProcess.Push((child, pathIndex, path));
                    }                    
                }

                if (node.IsBranch)
                {
                    pathIndex++;

                    int left = Bytes.Comparer.Compare(path.ToArray(), leftBoundary[0..path.Count()].ToArray()) == 0 ? leftBoundary[pathIndex] : 0;
                    int right = Bytes.Comparer.Compare(path.ToArray(), rightBoundary[0..path.Count()].ToArray()) == 0 ? rightBoundary[pathIndex] : 15;

                    for (int ci = left; ci <= right; ci++)
                    {
                        Keccak? childKeccak = node.GetChildHash(ci);

                        if (ci >= left && ci <= right)
                        {
                            node.SetChild(ci, null);
                        }

                        if (childKeccak is not null && (ci == left || ci == right) && dict.TryGetValue(childKeccak, out TrieNode child))
                        {
                            if (!child.IsLeaf)
                            {
                                node.SetChild(ci, child);

                                // TODO: we should optimize it - copy only if there are two boundary children
                                List<byte> newPath = new(path);

                                newPath.Add((byte)ci);

                                proofNodesToProcess.Push((child, pathIndex, newPath));
                            }
                        }
                    }

                }
            }

            return true;
        }

        private static Dictionary<Keccak, TrieNode> CreateProofDict(byte[][] proofs, ITrieStore store)
        {
            Dictionary<Keccak, TrieNode> dict = new();

            for (int i = 0; i < proofs.Length; i++)
            {
                byte[] proof = proofs[i];
                var node = new TrieNode(NodeType.Unknown, proof, true);
                node.IsBoundaryProofNode = true;
                node.ResolveNode(store);
                node.ResolveKey(store, i == 0);

                dict[node.Keccak] = node;
            }

            return dict;
        }

        private static void FillBoundaryTree(PatriciaTree tree, Keccak expectedRootHash, byte[][] proofs, Keccak startingHash, Keccak endHash)
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
