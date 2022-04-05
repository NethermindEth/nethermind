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

        public static (bool moreChildrenToRight, IList<PathWithAccount> storageRoots, IList<Keccak> codeHashes) AddAccountRange(StateTree tree, long blockNumber, Keccak expectedRootHash, Keccak startingHash, PathWithAccount[] accounts, byte[][] proofs = null)
        {
            // TODO: Check the accounts boundaries and sorting

            //var rlps = proofs.Select(p => $"{Keccak.Compute(p).ToString(false)}:{new Rlp(p).ToString(false)}").ToArray();
            //var res = string.Join($"{Environment.NewLine}{Environment.NewLine}", rlps);
            //var first = proofs.Select((p) => { var n = (new TrieNode(NodeType.Unknown, p, true)); n.ResolveNode(tree.TrieStore); return n; }) ;


            Keccak lastHash = accounts.Last().AddressHash;

            (Keccak _, Dictionary<Keccak, TrieNode> boundaryDict, bool moreChildrenToRight) = FillBoundaryTree(tree, startingHash, lastHash, expectedRootHash, proofs);

            IList<PathWithAccount> accountsWithStorage = new List<PathWithAccount>();
            IList<Keccak> codeHashes = new List<Keccak>();

            try
            {
                foreach (var account in accounts)
                {
                    if (account.Account.HasStorage)
                    {
                        accountsWithStorage.Add(account);
                    }

                    if(account.Account.HasCode)
                    {
                        codeHashes.Add(account.Account.CodeHash);
                    }

                    tree.Set(account.AddressHash, account.Account);
                }
            }
            catch (Exception ex)
            {
                throw;
            }

            tree.UpdateRootHash();

            if (tree.RootHash != expectedRootHash)
            {
                // TODO: log incorrect range
                return (true, null, null);
            }

            try
            {
                Interlocked.Exchange(ref _accCommitInProgress, 1);

                StitchBoundaries(boundaryDict, tree.TrieStore);

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

            return (moreChildrenToRight, accountsWithStorage, codeHashes);
        }

        public static (Keccak? rootHash, bool moreChildrenToRight) AddStorageRange(StorageTree tree, long blockNumber, Keccak startingHash, PathWithStorageSlot[] slots, Keccak expectedRootHash = null, byte[][] proofs = null)
        {
            // TODO: Check the slots boundaries and sorting

            Keccak lastHash = slots.Last().Path;

            (Keccak rootHash, Dictionary<Keccak, TrieNode> boundaryDict, bool moreChildrenToRight) = FillBoundaryTree(tree, startingHash, lastHash, expectedRootHash:expectedRootHash, proofs);

            // TODO: try-catch not needed, just for debug
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

            //if (tree.RootHash != rootHash)
            //{
            //    // TODO: log incorrect range
            //    return (Keccak.EmptyTreeHash, true); ;
            //}

            try
            {
                Interlocked.Exchange(ref _slotCommitInProgress, 1);

                StitchBoundaries(boundaryDict, tree.TrieStore);

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

            return (tree.RootHash, moreChildrenToRight);
        }

        private static (Keccak expectedRootHash, Dictionary<Keccak, TrieNode> boundaryDict, bool moreChildrenToRight) FillBoundaryTree(PatriciaTree tree, Keccak startingHash, Keccak endHash, Keccak expectedRootHash = null, byte[][] proofs = null)
        {
            if (proofs is null || proofs.Length == 0)
            {
                return (expectedRootHash, null, false);
            }

            if (tree == null)
            {
                throw new ArgumentNullException(nameof(tree));
            }

            startingHash ??= Keccak.Zero;

            (TrieNode root, Dictionary<Keccak, TrieNode> dict) = CreateProofDict(proofs, tree.TrieStore, expectedRootHash);

            Keccak rootHash = new Keccak(root.Keccak.Bytes);

            Dictionary<Keccak, TrieNode> processed = new();
            Span<byte> leftBoundary = stackalloc byte[64];
            Nibbles.BytesToNibbleBytes(startingHash.Bytes, leftBoundary);
            Span<byte> rightBoundary = stackalloc byte[64];
            Nibbles.BytesToNibbleBytes(endHash.Bytes, rightBoundary);

            Stack<(TrieNode parent, TrieNode node, int pathIndex, List<byte> path)> proofNodesToProcess = new();

            tree.RootRef = root;
            proofNodesToProcess.Push((null, root, -1, new List<byte>()));

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

            return (rootHash, dict, moreChildrenToRight);
        }

        private static (TrieNode root, Dictionary<Keccak, TrieNode> dict) CreateProofDict(byte[][] proofs, ITrieStore store, Keccak expectedRootHash = null)
        {
            TrieNode root = null;
            Dictionary<Keccak, TrieNode> dict = new();

            for (int i = 0; i < proofs.Length; i++)
            {
                byte[] proof = proofs[i];
                var node = new TrieNode(NodeType.Unknown, proof, true);
                node.IsBoundaryProofNode = true;
                node.ResolveNode(store);
                node.ResolveKey(store, i == 0);

                dict[node.Keccak] = node;

                if (i == 0 || expectedRootHash == node.Keccak)
                {
                    root = node;
                }
            }

            return (root, dict);
        }

        private static void StitchBoundaries(Dictionary<Keccak, TrieNode> boundaryDict, ITrieStore store)
        {
            if(boundaryDict == null || boundaryDict.Count == 0)
            {
                return;
            }

            foreach (var node in boundaryDict.Values)
            {
                if(!node.IsPersisted)
                {
                    if(node.IsExtension)
                    {
                        if(DoChildExist(node, 1, store))
                        {
                            node.IsBoundaryProofNode = false;
                        }
                    }

                    if (node.IsBranch)
                    {
                        bool isBoundaryProofNode = false;
                        for (int i = 0; i <= 15; i++)
                        {
                            if (!DoChildExist(node, i, store))
                            {
                                isBoundaryProofNode = true;
                                break;
                            }
                        }

                        node.IsBoundaryProofNode = isBoundaryProofNode;
                    }
                }
            }
        }

        private static bool DoChildExist(TrieNode node, int childIndex, ITrieStore store)
        {
            var data = node.GetData(childIndex) as TrieNode;
            if (data != null)
            {
                return true;
            }

            Keccak childKeccak = node.GetChildHash(childIndex);
            if(childKeccak is null)
            {
                return true;
            }

            return store.IsPersisted(childKeccak);
        }
    }
}
