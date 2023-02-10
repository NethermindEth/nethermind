// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie
{
    [DebuggerDisplay("{RootHash}")]
    public class PatriciaTreeByPath : Patricia
    {
        public PatriciaTreeByPath()
            : base(NullTrieStore.Instance, EmptyTreeHash, false, true, NullLogManager.Instance)
        {
        }

        public PatriciaTreeByPath(IKeyValueStoreWithBatching keyValueStore)
            : base(keyValueStore, EmptyTreeHash, false, true, NullLogManager.Instance)
        {
        }

        public PatriciaTreeByPath(ITrieStore trieStore, ILogManager logManager)
            : base(trieStore, EmptyTreeHash, false, true, logManager)
        {
        }

        public PatriciaTreeByPath(
            IKeyValueStoreWithBatching keyValueStore,
            Keccak rootHash,
            bool parallelBranches,
            bool allowCommits,
            ILogManager logManager)
            : base(
                new TrieStoreByPath(keyValueStore, logManager),
                rootHash,
                parallelBranches,
                allowCommits,
                logManager)
        {
        }

        public PatriciaTreeByPath(
            ITrieStore? trieStore,
            Keccak rootHash,
            bool parallelBranches,
            bool allowCommits,
            ILogManager? logManager) : base(trieStore, rootHash, parallelBranches, allowCommits, logManager)
        {
        }

        public override void SetRootHash(Keccak? value, bool resetObjects)
        {
            _rootHash = value ?? Keccak.EmptyTreeHash; // nulls were allowed before so for now we leave it this way
            if (_rootHash == Keccak.EmptyTreeHash)
            {
                RootRef = null;
            }
            else if (resetObjects)
            {
                RootRef = TrieStore.FindCachedOrUnknown(Array.Empty<byte>());
                RootRef.Keccak = _rootHash;
            }
        }

        internal override byte[]? Run(
            Span<byte> updatePath,
            int nibblesCount,
            byte[]? updateValue,
            bool isUpdate,
            bool ignoreMissingDelete = true,
            Keccak? startRootHash = null)
        {
            if (isUpdate && startRootHash is not null)
            {
                throw new InvalidOperationException("Only reads can be done in parallel on the Patricia tree");
            }

#if DEBUG
            if (nibblesCount != updatePath.Length)
            {
                throw new Exception("Does it ever happen?");
            }
#endif

            TraverseContext traverseContext =
                new(updatePath.Slice(0, nibblesCount), updateValue, isUpdate, ignoreMissingDelete);

            // lazy stack cleaning after the previous update
            if (traverseContext.IsUpdate)
            {
                _nodeStack.Clear();
            }

            byte[]? result;
            if (startRootHash is not null)
            {
                if (_logger.IsTrace) _logger.Trace($"Starting from {startRootHash} - {traverseContext.ToString()}");
                TrieNode startNode = TrieStore.FindCachedOrUnknown(startRootHash);
                //startNode.ResolveNode(TrieStore, traverseContext.GetCurrentPath());
                startNode.ResolveNode(TrieStore);
                result = TraverseNode(startNode, traverseContext);
            }
            else
            {
                bool trieIsEmpty = RootRef is null;
                if (trieIsEmpty)
                {
                    if (traverseContext.UpdateValue is not null)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Setting new leaf node with value {traverseContext.UpdateValue}");
                        HexPrefix key = HexPrefix.Leaf(updatePath.Slice(0, nibblesCount).ToArray());
                        RootRef = TrieNodeFactory.CreateLeaf(key, traverseContext.UpdateValue, EmptyKeyPath);
                    }

                    if (_logger.IsTrace) _logger.Trace($"Keeping the root as null in {traverseContext.ToString()}");
                    result = traverseContext.UpdateValue;
                }
                else
                {
                    //RootRef.ResolveNode(TrieStore, traverseContext.GetCurrentPath());
                    RootRef.ResolveNode(TrieStore);
                    if (_logger.IsTrace) _logger.Trace($"{traverseContext.ToString()}");
                    result = TraverseNode(RootRef, traverseContext);
                }
            }

            return result;
        }



        internal override byte[]? TraverseBranch(TrieNode node, TraverseContext traverseContext)
        {
            if (traverseContext.RemainingUpdatePathLength == 0)
            {
                /* all these cases when the path ends on the branch assume a trie with values in the branches
                   which is not possible within the Ethereum protocol which has keys of the same length (64) */

                if (traverseContext.IsRead)
                {
                    return node.Value;
                }

                if (traverseContext.IsDelete)
                {
                    if (node.Value is null)
                    {
                        return null;
                    }

                    ConnectNodes(null);
                }
                else if (Bytes.AreEqual(traverseContext.UpdateValue, node.Value))
                {
                    return traverseContext.UpdateValue;
                }
                else
                {
                    TrieNode withUpdatedValue = node.CloneWithChangedValue(traverseContext.UpdateValue);
                    ConnectNodes(withUpdatedValue);
                }

                return traverseContext.UpdateValue;
            }

            byte a = traverseContext.UpdatePath[61];
            TrieNode childNode = node.GetChild(TrieStore, traverseContext.UpdatePath.Slice(0, traverseContext.CurrentIndex + 1), traverseContext.UpdatePath[traverseContext.CurrentIndex]);
            if (traverseContext.IsUpdate)
            {
                _nodeStack.Push(new StackedNode(node, traverseContext.UpdatePath[traverseContext.CurrentIndex]));
            }

            traverseContext.CurrentIndex++;

            if (childNode is null)
            {
                if (traverseContext.IsRead)
                {
                    return null;
                }

                if (traverseContext.IsDelete)
                {
                    if (traverseContext.IgnoreMissingDelete)
                    {
                        return null;
                    }

                    throw new TrieException(
                        $"Could not find the leaf node to delete: {traverseContext.UpdatePath.ToHexString(false)}");
                }

                byte[] leafPath = traverseContext.UpdatePath.Slice(
                    traverseContext.CurrentIndex,
                    traverseContext.UpdatePath.Length - traverseContext.CurrentIndex).ToArray();
                TrieNode leaf = TrieNodeFactory.CreateLeaf(HexPrefix.Leaf(leafPath), traverseContext.UpdateValue, traverseContext.GetCurrentPath());
                ConnectNodes(leaf);

                return traverseContext.UpdateValue;
            }

            //childNode.ResolveNode(TrieStore, traverseContext.GetCurrentPath());
            childNode.ResolveNode(TrieStore);
            TrieNode nextNode = childNode;
            return TraverseNode(nextNode, traverseContext);
        }

        internal override byte[]? TraverseLeaf(TrieNode node, TraverseContext traverseContext)
        {
            if (node.Path is null)
            {
                throw new InvalidDataException("An attempt to visit a node without a prefix path.");
            }

            Span<byte> remaining = traverseContext.GetRemainingUpdatePath();
            Span<byte> shorterPath;
            Span<byte> longerPath;
            if (traverseContext.RemainingUpdatePathLength - node.Path.Length < 0)
            {
                shorterPath = remaining;
                longerPath = node.Path;
            }
            else
            {
                shorterPath = node.Path;
                longerPath = remaining;
            }

            byte[] shorterPathValue;
            byte[] longerPathValue;

            if (Bytes.AreEqual(shorterPath, node.Path))
            {
                shorterPathValue = node.Value;
                longerPathValue = traverseContext.UpdateValue;
            }
            else
            {
                shorterPathValue = traverseContext.UpdateValue;
                longerPathValue = node.Value;
            }

            int extensionLength = FindCommonPrefixLength(shorterPath, longerPath);
            if (extensionLength == shorterPath.Length && extensionLength == longerPath.Length)
            {
                if (traverseContext.IsRead)
                {
                    return node.Value;
                }

                if (traverseContext.IsDelete)
                {
                    ConnectNodes(null);
                    return traverseContext.UpdateValue;
                }

                if (!Bytes.AreEqual(node.Value, traverseContext.UpdateValue))
                {
                    TrieNode withUpdatedValue = node.CloneWithChangedValue(traverseContext.UpdateValue);
                    ConnectNodes(withUpdatedValue);
                    return traverseContext.UpdateValue;
                }

                return traverseContext.UpdateValue;
            }

            if (traverseContext.IsRead)
            {
                return null;
            }

            if (traverseContext.IsDelete)
            {
                if (traverseContext.IgnoreMissingDelete)
                {
                    return null;
                }

                throw new TrieException(
                    $"Could not find the leaf node to delete: {traverseContext.UpdatePath.ToHexString(false)}");
            }

            if (extensionLength != 0)
            {
                Span<byte> extensionPath = longerPath.Slice(0, extensionLength);
                TrieNode extension = TrieNodeFactory.CreateExtension(HexPrefix.Extension(extensionPath.ToArray()), traverseContext.GetCurrentPath());
                _nodeStack.Push(new StackedNode(extension, 0));
            }

            TrieNode branch = TrieNodeFactory.CreateBranch(traverseContext.UpdatePath.Slice(0, traverseContext.CurrentIndex + extensionLength).ToArray());
            if (extensionLength == shorterPath.Length)
            {
                branch.Value = shorterPathValue;
            }
            else
            {
                Span<byte> shortLeafPath = shorterPath.Slice(extensionLength + 1, shorterPath.Length - extensionLength - 1);
                TrieNode shortLeaf;
                if (shorterPath.Length == 64)
                {
                    Span<byte> pathToShortLeaf = shorterPath.Slice(0, extensionLength + 1);
                    shortLeaf = TrieNodeFactory.CreateLeaf(HexPrefix.Leaf(shortLeafPath.ToArray()), shorterPathValue, pathToShortLeaf);
                }
                else
                {
                    Span<byte> pathToShortLeaf = stackalloc byte[branch.PathToNode.Length + 1];
                    branch.PathToNode.CopyTo(pathToShortLeaf);
                    pathToShortLeaf[branch.PathToNode.Length] = shorterPath[extensionLength];
                    shortLeaf = TrieNodeFactory.CreateLeaf(HexPrefix.Leaf(shortLeafPath.ToArray()), shorterPathValue, pathToShortLeaf);
                }
                branch.SetChild(shorterPath[extensionLength], shortLeaf);
            }

            Span<byte> leafPath = longerPath.Slice(extensionLength + 1, longerPath.Length - extensionLength - 1);
            Span<byte> pathToLeaf = stackalloc byte[branch.PathToNode.Length + 1];
            branch.PathToNode.CopyTo(pathToLeaf);
            pathToLeaf[branch.PathToNode.Length] = longerPath[extensionLength];
            TrieNode withUpdatedKeyAndValue = node.CloneWithChangedKeyAndValue(
                HexPrefix.Leaf(leafPath.ToArray()), longerPathValue, pathToLeaf.ToArray());

            _nodeStack.Push(new StackedNode(branch, longerPath[extensionLength]));
            ConnectNodes(withUpdatedKeyAndValue);

            return traverseContext.UpdateValue;
        }

        internal  override byte[]? TraverseExtension(TrieNode node, TraverseContext traverseContext)
        {
            if (node.Path is null)
            {
                throw new InvalidDataException("An attempt to visit a node without a prefix path.");
            }

            TrieNode originalNode = node;
            Span<byte> remaining = traverseContext.GetRemainingUpdatePath();

            int extensionLength = FindCommonPrefixLength(remaining, node.Path);
            if (extensionLength == node.Path.Length)
            {
                traverseContext.CurrentIndex += extensionLength;
                if (traverseContext.IsUpdate)
                {
                    _nodeStack.Push(new StackedNode(node, 0));
                }

                TrieNode next = node.GetChild(TrieStore, traverseContext.GetCurrentPath(), 0);
                if (next is null)
                {
                    throw new TrieException(
                        $"Found an {nameof(NodeType.Extension)} {node.Keccak} that is missing a child.");
                }

                //next.ResolveNode(TrieStore, traverseContext.GetCurrentPath());
                next.ResolveNode(TrieStore);
                return TraverseNode(next, traverseContext);
            }

            if (traverseContext.IsRead)
            {
                return null;
            }

            if (traverseContext.IsDelete)
            {
                if (traverseContext.IgnoreMissingDelete)
                {
                    return null;
                }

                throw new TrieException(
                    $"Could find the leaf node to delete: {traverseContext.UpdatePath.ToHexString()}");
            }

            byte[] pathBeforeUpdate = node.Path;
            if (extensionLength != 0)
            {
                byte[] extensionPath = node.Path.Slice(0, extensionLength);
                node = node.CloneWithChangedKey(HexPrefix.Extension(extensionPath));
                _nodeStack.Push(new StackedNode(node, 0));
            }

            TrieNode branch = TrieNodeFactory.CreateBranch(traverseContext.UpdatePath.Slice(0, traverseContext.CurrentIndex + extensionLength));
            if (extensionLength == remaining.Length)
            {
                branch.Value = traverseContext.UpdateValue;
            }
            else
            {
                byte[] path = remaining.Slice(extensionLength + 1, remaining.Length - extensionLength - 1).ToArray();
                TrieNode shortLeaf = TrieNodeFactory.CreateLeaf(HexPrefix.Leaf(path), traverseContext.UpdateValue, traverseContext.UpdatePath.Slice(0, traverseContext.CurrentIndex + extensionLength + 1));
                branch.SetChild(remaining[extensionLength], shortLeaf);
            }

            TrieNode originalNodeChild = originalNode.GetChild(TrieStore, 0);
            if (originalNodeChild is null)
            {
                throw new InvalidDataException(
                    $"Extension {originalNode.Keccak} has no child.");
            }

            if (pathBeforeUpdate.Length - extensionLength > 1)
            {
                byte[] extensionPath = pathBeforeUpdate.Slice(extensionLength + 1, pathBeforeUpdate.Length - extensionLength - 1);
                Span<byte> fullPath = traverseContext.UpdatePath.Slice(0, traverseContext.CurrentIndex + extensionLength + 1);
                fullPath[traverseContext.CurrentIndex + extensionLength] = pathBeforeUpdate[extensionLength];
                TrieNode secondExtension
                    = TrieNodeFactory.CreateExtension(HexPrefix.Extension(extensionPath), originalNodeChild, fullPath);
                branch.SetChild(pathBeforeUpdate[extensionLength], secondExtension);
            }
            else
            {
                TrieNode childNode = originalNodeChild;
                branch.SetChild(pathBeforeUpdate[extensionLength], childNode);
            }

            ConnectNodes(branch);
            return traverseContext.UpdateValue;
        }



    }
}
