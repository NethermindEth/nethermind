using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
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
        private static object _syncCommit = new();

        private static int _accCommitInProgress = 0;
        private static int _slotCommitInProgress = 0;

        public static (Keccak? rootHash, bool moreChildrenToRight, IList<PathWithAccount> storageRoots) AddAccountRange(StateTree tree, long blockNumber, Keccak expectedRootHash, Keccak startingHash, PathWithAccount[] accounts, byte[][] proofs = null)
        {
            // TODO: Check the accounts boundaries and sorting

            //var rlps = proofs.Select(p => $"{Keccak.Compute(p).ToString(false)}:{new Rlp(p).ToString(false)}").ToArray();
            //var res = string.Join($"{Environment.NewLine}{Environment.NewLine}", rlps);
            //var first = proofs.Select((p) => { var n = (new TrieNode(NodeType.Unknown, p, true)); n.ResolveNode(tree.TrieStore); return n; }) ;


            Keccak lastHash = accounts.Last().AddressHash;

            (bool success, bool moreChildrenToRight) = FillBoundaryTree(tree, expectedRootHash, startingHash, lastHash, proofs);

            IList<PathWithAccount> accountsWithStorage = null;
            if (success)
            {
                accountsWithStorage = new List<PathWithAccount>();

                foreach (var account in accounts)
                {
                    if(account.Account.HasStorage)
                    {
                        accountsWithStorage.Add(account);
                    }

                    tree.Set(account.AddressHash, account.Account);
                }

                tree.UpdateRootHash();

                if (tree.RootHash != expectedRootHash)
                {
                    // TODO: log incorrect range
                    return (Keccak.EmptyTreeHash, true, null);
                }

                try
                {
                    Interlocked.Exchange(ref _accCommitInProgress, 1);

                    lock (_syncCommit)
                    {
                        tree.Commit(blockNumber);
                    }

                    Interlocked.Exchange(ref _accCommitInProgress, 0);
                }
                catch (Exception ex)
                {

                    throw new Exception($"{ex.Message}, _accCommitInProgress:{_accCommitInProgress}, _slotCommitInProgress:{_slotCommitInProgress}", ex);
                }
            }

            return (tree.RootHash, moreChildrenToRight, accountsWithStorage);
        }

        public static (Keccak? rootHash, bool moreChildrenToRight) AddStorageRange(StorageTree tree, long blockNumber, Keccak expectedRootHash, Keccak startingHash, PathWithStorageSlot[] slots, byte[][] proofs = null)
        {
            // TODO: Check the slots boundaries and sorting

            Keccak lastHash = slots.Last().Path;

            string expRootHash = expectedRootHash.ToString();

            (bool success, bool moreChildrenToRight) = FillBoundaryTree(tree, expectedRootHash, startingHash, lastHash, proofs);

            if (success)
            {
                try
                {
                    foreach (var slot in slots)
                    {
                        tree.Set(slot.Path, slot.SlotRlpValue, false);
                    }
                }
                catch(Exception ex)
                {
                    throw;
                }

                tree.UpdateRootHash();

                if (tree.RootHash != expectedRootHash)
                {
                    // TODO: log incorrect range
                    return (Keccak.EmptyTreeHash, true); ;
                }

                try
                {
                    Interlocked.Exchange(ref _slotCommitInProgress, 1);

                    lock (_syncCommit)
                    {
                        tree.Commit(blockNumber);
                    }

                    Interlocked.Exchange(ref _slotCommitInProgress, 0);
                }
                catch (Exception ex)
                {
                    throw new Exception($"{ex.Message}, _accCommitInProgress:{_accCommitInProgress}, _slotCommitInProgress:{_slotCommitInProgress}", ex);
                }
            }

            return (tree.RootHash, moreChildrenToRight);
        }

        private static (bool success, bool moreChildrenToRight) FillBoundaryTree(PatriciaTree tree, Keccak expectedRootHash, Keccak startingHash, Keccak endHash, byte[][] proofs = null)
        {
            if (proofs is null || proofs.Length == 0)
            {
                return (true, false);
            }

            if (tree == null)
            {
                throw new ArgumentNullException(nameof(tree));
            }

            startingHash ??= Keccak.Zero;

            Dictionary<Keccak, TrieNode> dict = CreateProofDict(proofs, tree.TrieStore);

            Dictionary<Keccak, TrieNode> processed = new();
            Span<byte> leftBoundary = stackalloc byte[64];
            Nibbles.BytesToNibbleBytes(startingHash.Bytes, leftBoundary);
            Span<byte> rightBoundary = stackalloc byte[64];
            Nibbles.BytesToNibbleBytes(endHash.Bytes, rightBoundary);

            Stack<(TrieNode parent, TrieNode node, int pathIndex, List<byte> path)> proofNodesToProcess = new();

            if(dict.TryGetValue(expectedRootHash, out TrieNode root))
            {
                tree.RootRef = root;

                proofNodesToProcess.Push((null, root, -1, new List<byte>()));
            }
            else
            {
                return (false, true);
            }

            bool moreChildrenToRight = false;

            while (proofNodesToProcess.Count > 0)
            {
                (TrieNode parent, TrieNode node, int pathIndex, List<byte> path) = proofNodesToProcess.Pop();

                if (node.IsExtension)
                {
                    Keccak? childKeccak = node.GetChildHash(0);

                    if (childKeccak is not null)
                    {
                        if (dict.TryGetValue(childKeccak, out TrieNode child))
                        {
                            node.SetChild(0, child);

                            pathIndex += node.Path.Length;
                            path.AddRange(node.Path);
                            proofNodesToProcess.Push((node, child, pathIndex, path));
                        }
                        else
                        {
                            if(Bytes.Comparer.Compare(path.ToArray(), leftBoundary[0..path.Count()].ToArray()) >= 0 
                                && parent is not null
                                && parent.IsBranch)
                            {
                                for (int i = 0; i < 15; i++)
                                {
                                    Keccak? kec = parent.GetChildHash(i);
                                    if(kec == node.Keccak)
                                    {
                                        parent.SetChild(i, null);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }

                if (node.IsBranch)
                {
                    pathIndex++;

                    int left = Bytes.Comparer.Compare(path.ToArray(), leftBoundary[0..path.Count()].ToArray()) == 0 ? leftBoundary[pathIndex] : 0;
                    int right = Bytes.Comparer.Compare(path.ToArray(), rightBoundary[0..path.Count()].ToArray()) == 0 ? rightBoundary[pathIndex] : 15;

                    int maxIndex = moreChildrenToRight ? right : 15;

                    for (int ci = left; ci <= maxIndex; ci++)
                    {
                        Keccak? childKeccak = node.GetChildHash(ci);

                        moreChildrenToRight |= ci > right && childKeccak is not null;

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

                                proofNodesToProcess.Push((node, child, pathIndex, newPath));
                            }
                        }
                    }
                }
            }

            return (true, moreChildrenToRight);
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

        private static void FillBoundaryTree_old(PatriciaTree tree, Keccak expectedRootHash, byte[][] proofs, Keccak startingHash, Keccak endHash)
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
