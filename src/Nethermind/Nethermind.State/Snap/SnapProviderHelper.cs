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

            var rlps = proofs.Select(p => $"{Keccak.Compute(p).ToString(false)}:{new Rlp(p).ToString(false)}").ToArray();

            var res = string.Join($"{Environment.NewLine}{Environment.NewLine}", rlps);

            var first = proofs.Select((p) => { var n = (new TrieNode(NodeType.Unknown, p, true)); n.ResolveNode(tree.TrieStore); return n; }) ;


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

                FillBoundaryTree(tree, expectedRootHash, proofs, startingHash, lastHash);
            }

            return true;
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
