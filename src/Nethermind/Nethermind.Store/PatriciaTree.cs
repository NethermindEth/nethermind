/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

namespace Nethermind.Store
{
    [DebuggerDisplay("{RootHash}")]
    public class PatriciaTree
    {
        private static readonly LruCache<Keccak, Rlp> NodeCache = new LruCache<Keccak, Rlp>(64 * 1024);
        private static readonly LruCache<byte[], byte[]> ValueCache = new LruCache<byte[], byte[]>(128 * 1024);

        /// <summary>
        ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
        /// </summary>
        public static readonly Keccak EmptyTreeHash = Keccak.EmptyTreeHash;

        /// <summary>
        /// Note at the moment this can be static because we never add to any two different Patricia trees in parallel
        /// THis would be receipts, transactions, state, storage - all of them are sequential so only on etree at the time uses NodeStack
        /// </summary>
        private static readonly Stack<StackedNode> NodeStack = new Stack<StackedNode>(); // TODO: if switching to parallel then need to pool tree operations with separate node stacks?, if...

        private static readonly ConcurrentQueue<Exception> CommitExceptions = new ConcurrentQueue<Exception>();

        private readonly IDb _db;
        private readonly bool _parallelizeBranches;

        private Keccak _rootHash;

        internal NodeRef RootRef;

        public PatriciaTree()
            : this(NullDb.Instance, EmptyTreeHash, false)
        {
        }

        public PatriciaTree(IDb db, Keccak rootHash, bool parallelizeBranches)
        {
            _db = db;
            _parallelizeBranches = parallelizeBranches;
            RootHash = rootHash;
        }

        internal Node Root
        {
            get
            {
                RootRef?.ResolveNode(this);
                return RootRef?.Node;
            }
        }

        public Keccak RootHash
        {
            get => _rootHash;
            set => SetRootHash(value, true);
        }

        public void Commit(bool wrapInBatch = true)
        {
            if (RootRef == null)
            {
                return;
            }

            if (RootRef.IsDirty)
            {
                CurrentCommit.Clear();
                Commit(RootRef, true);
                foreach (NodeRef nodeRef in CurrentCommit)
                {
                    _db.Set(nodeRef.KeccakOrRlp.GetOrComputeKeccak(), nodeRef.FullRlp.Bytes);
                }

                // reset objects
                Keccak keccak = RootRef.KeccakOrRlp.GetOrComputeKeccak();
                SetRootHash(keccak, true);
            }
        }

        private static readonly ConcurrentBag<NodeRef> CurrentCommit = new ConcurrentBag<NodeRef>();

        private void Commit(NodeRef nodeRef, bool isRoot)
        {
            Node node = nodeRef.Node;
            if (node is Branch branch)
            {
                // idea from EthereumJ - testing parallel branches
                if (!_parallelizeBranches || !isRoot)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        NodeRef subnode = branch.Nodes[i];
                        if (subnode?.IsDirty ?? false)
                        {
                            Commit(branch.Nodes[i], false);
                        }
                    }
                }
                else
                {
                    List<NodeRef> nodesToCommit = new List<NodeRef>();
                    for (int i = 0; i < 16; i++)
                    {
                        NodeRef subnode = branch.Nodes[i];
                        if (subnode?.IsDirty ?? false)
                        {
                            nodesToCommit.Add(branch.Nodes[i]);
                        }
                    }

                    if (nodesToCommit.Count >= 4)
                    {
                        CommitExceptions.Clear();
                        Parallel.For(0, nodesToCommit.Count, i =>
                        {
                            try
                            {
                                Commit(nodesToCommit[i], false);
                            }
                            catch (Exception e)
                            {
                                CommitExceptions.Enqueue(e);
                            }
                        });

                        if (CommitExceptions.Count > 0)
                        {
                            throw new AggregateException(CommitExceptions);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < nodesToCommit.Count; i++)
                        {
                            Commit(nodesToCommit[i], false);
                        }
                    }
                }
            }
            else if (node is Extension extension)
            {
                if (extension.NextNodeRef.IsDirty)
                {
                    Commit(extension.NextNodeRef, false);
                }
            }

            node.IsDirty = false;
            nodeRef.ResolveKey();
            if (nodeRef.KeccakOrRlp.IsKeccak || isRoot)
            {
                Keccak keccak = nodeRef.KeccakOrRlp.GetOrComputeKeccak();
                NodeCache.Set(keccak, nodeRef.FullRlp);
                CurrentCommit.Add(nodeRef);
            }
        }

        public void UpdateRootHash()
        {
            RootRef?.ResolveKey();
            SetRootHash(RootRef?.KeccakOrRlp.GetOrComputeKeccak() ?? EmptyTreeHash, false);
        }

        private void SetRootHash(Keccak value, bool resetObjects)
        {
            if (_rootHash == value)
            {
                return;
            }

            _rootHash = value;
            if (_rootHash == Keccak.EmptyTreeHash)
            {
                RootRef = null;
            }
            else
            {
                if (resetObjects)
                {
                    RootRef = new NodeRef(new KeccakOrRlp(_rootHash), true);
                }
            }
        }

        private static Rlp RlpEncode(NodeRef nodeRef)
        {
            if (nodeRef == null)
            {
                return Rlp.OfEmptyByteArray;
            }

            nodeRef.ResolveKey();

            return nodeRef.KeccakOrRlp.GetOrEncodeRlp();
        }

        private static Rlp RlpEncodeNoStreams(Branch branch)
        {
            return Rlp.Encode(
                    RlpEncode(branch.Nodes[0]),
                    RlpEncode(branch.Nodes[1]),
                    RlpEncode(branch.Nodes[2]),
                    RlpEncode(branch.Nodes[3]),
                    RlpEncode(branch.Nodes[4]),
                    RlpEncode(branch.Nodes[5]),
                    RlpEncode(branch.Nodes[6]),
                    RlpEncode(branch.Nodes[7]),
                    RlpEncode(branch.Nodes[8]),
                    RlpEncode(branch.Nodes[9]),
                    RlpEncode(branch.Nodes[10]),
                    RlpEncode(branch.Nodes[11]),
                    RlpEncode(branch.Nodes[12]),
                    RlpEncode(branch.Nodes[13]),
                    RlpEncode(branch.Nodes[14]),
                    RlpEncode(branch.Nodes[15]),
                    Rlp.Encode(branch.Value)
                );
        }

        private static Rlp RlpEncode(Branch branch)
        {
            int contentLength = 0;
            for (int i = 0; i < 16; i++)
            {
                NodeRef nodeRef = branch.Nodes[i];
                if (nodeRef == null)
                {
                    contentLength += Rlp.LengthOfEmptyArrayRlp;
                }
                else
                {
                    nodeRef.ResolveKey();
                    if (nodeRef.KeccakOrRlp.IsKeccak)
                    {
                        contentLength += Rlp.LengthOfKeccakRlp;
                    }
                    else
                    {
                        contentLength += nodeRef.KeccakOrRlp.GetRlpOrThrow().Length;
                    }
                }
            }

            contentLength += Rlp.LengthOfByteArray(branch.Value);
            int sequenceLength = Rlp.GetSequenceRlpLength(contentLength);
            byte[] result = new byte[sequenceLength];
            int position = Rlp.StartSequence(result, 0, contentLength);
            for (int i = 0; i < 16; i++)
            {
                NodeRef nodeRef = branch.Nodes[i];
                if (nodeRef == null)
                {
                    result[position++] = Rlp.OfEmptyByteArray[0];
                }
                else
                {
                    if (nodeRef.KeccakOrRlp.IsKeccak)
                    {
                        result[position] = 160;
                        Array.Copy(nodeRef.KeccakOrRlp.GetKeccakOrThrow().Bytes, 0, result, position + 1, 32);
                        position += Rlp.LengthOfKeccakRlp;
                    }
                    else
                    {
                        byte[] rlpBytes = nodeRef.KeccakOrRlp.GetRlpOrThrow().Bytes;
                        Array.Copy(rlpBytes, 0, result, position, rlpBytes.Length);
                        position += rlpBytes.Length;
                    }
                }
            }

            Rlp.Encode(result, position, branch.Value);
            return new Rlp(result);
        }

        internal static Rlp RlpEncode(Node node)
        {
            Metrics.TreeNodeRlpEncodings++;
            if (node is Leaf leaf)
            {
                Rlp result = Rlp.Encode(Rlp.Encode(leaf.Key.ToBytes()), Rlp.Encode(leaf.Value));
                return result;
            }

            if (node is Branch branch)
            {
                return RlpEncode(branch);
            }

            if (node is Extension extension)
            {
                return Rlp.Encode(
                    Rlp.Encode(extension.Key.ToBytes()),
                    RlpEncode(extension.NextNodeRef));
            }

            throw new InvalidOperationException($"Unknown node type {node?.GetType().Name}");
        }

        internal static Node RlpDecode(Rlp bytes)
        {
            Metrics.TreeNodeRlpDecodings++;
            Rlp.DecoderContext context = bytes.Bytes.AsRlpContext();

            context.ReadSequenceLength();
            int numberOfItems = context.ReadNumberOfItemsRemaining();

            Node result;
            if (numberOfItems == 17)
            {
                NodeRef[] nodes = new NodeRef[16];
                for (int i = 0; i < 16; i++)
                {
                    nodes[i] = DecodeChildNode(context);
                }

                byte[] value = context.DecodeByteArray();
                Branch branch = new Branch(nodes, value);
                result = branch;
            }
            else if (numberOfItems == 2)
            {
                HexPrefix key = HexPrefix.FromBytes(context.DecodeByteArray());
                bool isExtension = key.IsExtension;
                if (isExtension)
                {
                    Extension extension = new Extension(key, DecodeChildNode(context));
                    result = extension;
                }
                else
                {
                    Leaf leaf = new Leaf(key, context.DecodeByteArray());
                    result = leaf;
                }
            }
            else
            {
                throw new InvalidOperationException($"Unexpected number of items = {numberOfItems} when decoding a node");
            }

            return result;
        }

        private static NodeRef DecodeChildNode(Rlp.DecoderContext decoderContext)
        {
            if (decoderContext.IsSequenceNext())
            {
                byte[] sequenceBytes = decoderContext.ReadSequenceRlp();
                if (sequenceBytes.Length >= 32)
                {
                    throw new InvalidOperationException();
                }

                KeccakOrRlp keccakOrRlp = new KeccakOrRlp(new Rlp(sequenceBytes));
                return new NodeRef(keccakOrRlp);
            }

            Keccak keccak = decoderContext.DecodeKeccak();
            return keccak == null ? null : new NodeRef(new KeccakOrRlp(keccak));
        }

        [DebuggerStepThrough]
        public void Set(Nibble[] nibbles, Rlp rlp)
        {
            throw new NotSupportedException();
            Set(nibbles, rlp.Bytes);
        }

        [DebuggerStepThrough]
        public virtual void Set(Nibble[] nibbles, byte[] value)
        {
            throw new NotSupportedException();
            Run(nibbles.ToLooseByteArray(), value, true);
        }

        public byte[] Get(byte[] rawKey)
        {
            byte[] value = ValueCache.Get(rawKey);
            if (value != null)
            {
                return value;
            }

            return Run(Nibbles.BytesToNibbleBytes(rawKey), null, false);
        }

        [DebuggerStepThrough]
        public void Set(byte[] rawKey, byte[] value)
        {
            ValueCache.Delete(rawKey);
            Run(Nibbles.BytesToNibbleBytes(rawKey), value, true);
        }

        [DebuggerStepThrough]
        public void Set(byte[] rawKey, Rlp value)
        {
            ValueCache.Delete(rawKey);
            Run(Nibbles.BytesToNibbleBytes(rawKey), value == null ? new byte[0] : value.Bytes, true);
        }

        internal Node GetNode(KeccakOrRlp keccakOrRlp)
        {
            Rlp rlp;
            if (keccakOrRlp.IsKeccak)
            {
                Keccak keccak = keccakOrRlp.GetOrComputeKeccak();
                rlp = NodeCache.Get(keccak) ?? new Rlp(_db[keccak.Bytes]);
            }
            else
            {
                rlp = new Rlp(keccakOrRlp.Bytes);
            }

            return RlpDecode(rlp);
        }

        public byte[] Run(byte[] updatePath, byte[] updateValue, bool isUpdate, bool ignoreMissingDelete = true)
        {
            if (isUpdate)
            {
                NodeStack.Clear();
            }

            if (isUpdate && updateValue.Length == 0)
            {
                updateValue = null;
            }

            if (RootRef == null)
            {
                if (!isUpdate || updateValue == null)
                {
                    return null;
                }

                Leaf leaf = new Leaf(new HexPrefix(true, updatePath), updateValue);
                leaf.IsDirty = true;
                RootRef = new NodeRef(leaf, true);
                return updateValue;
            }

            RootRef.ResolveNode(this);
            TraverseContext context = new TraverseContext(updatePath, updateValue, isUpdate, ignoreMissingDelete);
            return TraverseNode(RootRef.Node, context);
        }

        private byte[] TraverseNode(Node node, TraverseContext context)
        {
            if (node is Leaf leaf)
            {
                return TraverseLeaf(leaf, context);
            }

            if (node is Branch branch)
            {
                return TraverseBranch(branch, context);
            }

            if (node is Extension extension)
            {
                return TraverseExtension(extension, context);
            }

            throw new NotImplementedException($"Unknown node type {typeof(Node).Name}");
        }

        // TODO: this can be removed now but is lower priority temporarily while the patricia rewrite testing is in progress
        private void ConnectNodes(Node node)
        {
            //            Keccak previousRootHash = _tree.RootHash;

            bool isRoot = NodeStack.Count == 0;
            NodeRef nextNodeRef = node == null ? null : new NodeRef(node, isRoot);
            Node nextNode = node;

            // nodes should immutable here I guess
            while (!isRoot)
            {
                StackedNode parentOnStack = NodeStack.Pop();
                node = parentOnStack.Node;

                isRoot = NodeStack.Count == 0;

                if (node is Leaf leaf)
                {
                    throw new InvalidOperationException($"{nameof(Leaf)} {leaf} cannot be a parent of {nextNodeRef}");
                }

                if (node is Branch branch)
                {
                    //                    _tree.DeleteNode(branch.Nodes[parentOnStack.PathIndex], true);
                    if (!(nextNodeRef == null && !branch.IsValidWithOneNodeLess))
                    {
                        Branch newBranch = new Branch();
                        newBranch.IsDirty = true;
                        for (int i = 0; i < 16; i++)
                        {
                            newBranch.Nodes[i] = branch.Nodes[i];
                        }

                        newBranch.Value = branch.Value;
                        newBranch.Nodes[parentOnStack.PathIndex] = nextNodeRef;

                        nextNodeRef = new NodeRef(newBranch, isRoot);
                        nextNode = newBranch;
                    }
                    else
                    {
                        if (branch.Value.Length != 0)
                        {
                            Leaf leafFromBranch = new Leaf(new HexPrefix(true), branch.Value);
                            leafFromBranch.IsDirty = true;
                            nextNodeRef = new NodeRef(leafFromBranch, isRoot);
                            nextNode = leafFromBranch;
                        }
                        else
                        {
                            int childNodeIndex = 0;
                            for (int i = 0; i < 16; i++)
                            {
                                if (i != parentOnStack.PathIndex && branch.Nodes[i] != null)
                                {
                                    childNodeIndex = i;
                                    break;
                                }
                            }

                            NodeRef childNodeRef = branch.Nodes[childNodeIndex];
                            if (childNodeRef == null)
                            {
                                throw new InvalidOperationException("Before updating branch should have had at least two non-empty children");
                            }

                            // need to restore this node now?
                            if (childNodeRef.Node == null)
                            {
                            }

                            childNodeRef.ResolveNode(this);
                            Node childNode = childNodeRef.Node;
                            if (childNode is Branch)
                            {
                                Extension extensionFromBranch = new Extension(new HexPrefix(false, (byte)childNodeIndex), childNodeRef);
                                extensionFromBranch.IsDirty = true;
                                nextNodeRef = new NodeRef(extensionFromBranch, isRoot);
                                nextNode = extensionFromBranch;
                            }
                            else if (childNode is Extension childExtension)
                            {
                                //                                _tree.DeleteNode(childNodeHash, true);
                                Extension extensionFromBranch = new Extension(new HexPrefix(false, Bytes.Concat((byte)childNodeIndex, childExtension.Path)), childExtension.NextNodeRef);
                                extensionFromBranch.IsDirty = true;
                                nextNodeRef = new NodeRef(extensionFromBranch, isRoot);
                                nextNode = extensionFromBranch;
                            }
                            else if (childNode is Leaf childLeaf)
                            {
                                //                                _tree.DeleteNode(childNodeHash, true);
                                Leaf leafFromBranch = new Leaf(new HexPrefix(true, Bytes.Concat((byte)childNodeIndex, childLeaf.Path)), childLeaf.Value);
                                leafFromBranch.IsDirty = true;
                                nextNodeRef = new NodeRef(leafFromBranch, isRoot);
                                nextNode = leafFromBranch;
                            }
                            else
                            {
                                throw new InvalidOperationException($"Unknown node type {nextNode.GetType().Name}");
                            }
                        }
                    }
                }
                else if (node is Extension extension)
                {
                    //                    _tree.DeleteNode(extension.NextNodeRef, true);
                    if (nextNode is Leaf childLeaf)
                    {
                        Leaf leafFromExtension = new Leaf(new HexPrefix(true, Bytes.Concat(extension.Path, childLeaf.Path)), childLeaf.Value);
                        leafFromExtension.IsDirty = true;
                        nextNodeRef = new NodeRef(leafFromExtension, isRoot);
                        nextNode = leafFromExtension;
                    }
                    else if (nextNode is Extension childExtension)
                    {
                        Extension extensionFromExtension = new Extension(new HexPrefix(false, Bytes.Concat(extension.Path, childExtension.Path)), childExtension.NextNodeRef);
                        extensionFromExtension.IsDirty = true;
                        nextNodeRef = new NodeRef(extensionFromExtension, isRoot);
                        nextNode = extensionFromExtension;
                    }
                    else if (nextNode is Branch)
                    {
                        Extension newExtension = new Extension(extension.Key);
                        newExtension.IsDirty = true;
                        newExtension.NextNodeRef = nextNodeRef;
                        nextNodeRef = new NodeRef(newExtension, isRoot);
                        nextNode = newExtension;
                    }
                    else
                    {
                        throw new InvalidOperationException($"Unknown node type {nextNode?.GetType().Name}");
                    }
                }
                else
                {
                    throw new InvalidOperationException($"Unknown node type {node.GetType().Name}");
                }
            }

            if (!nextNodeRef?.IsRoot ?? false)
            {
                throw new InvalidOperationException("Non-root being made root");
            }

            RootRef = nextNodeRef;

            //            _tree.DeleteNode(new KeccakOrRlp(previousRootHash), true);
        }

        private byte[] TraverseBranch(Branch node, TraverseContext context)
        {
            if (context.RemainingUpdatePathLength == 0)
            {
                if (!context.IsUpdate)
                {
                    return node.Value;
                }

                if (context.UpdateValue == null)
                {
                    if (node.Value == null)
                    {
                        return null;
                    }

                    ConnectNodes(null);
                }
                else if (Bytes.UnsafeCompare(context.UpdateValue, node.Value))
                {
                    return context.UpdateValue;
                }
                else
                {
                    Branch newBranch = new Branch(node.Nodes, context.UpdateValue);
                    newBranch.IsDirty = true;
                    ConnectNodes(newBranch);
                }

                return context.UpdateValue;
            }

            NodeRef nextNodeRef = node.Nodes[context.UpdatePath[context.CurrentIndex]];
            if (context.IsUpdate)
            {
                NodeStack.Push(new StackedNode(node, context.UpdatePath[context.CurrentIndex]));
            }

            context.CurrentIndex++;

            if (nextNodeRef == null)
            {
                if (!context.IsUpdate)
                {
                    return null;
                }

                if (context.UpdateValue == null)
                {
                    if (context.IgnoreMissingDelete)
                    {
                        return null;
                    }

                    throw new InvalidOperationException($"Could not find the leaf node to delete: {Hex.FromBytes(context.UpdatePath, false)}");
                }

                byte[] leafPath = context.UpdatePath.Slice(context.CurrentIndex, context.UpdatePath.Length - context.CurrentIndex);
                Leaf leaf = new Leaf(new HexPrefix(true, leafPath), context.UpdateValue);
                leaf.IsDirty = true;
                ConnectNodes(leaf);

                return context.UpdateValue;
            }

            nextNodeRef.ResolveNode(this);
            Node nextNode = nextNodeRef.Node;
            return TraverseNode(nextNode, context);
        }

        private byte[] TraverseLeaf(Leaf node, TraverseContext context)
        {
            byte[] remaining = context.GetRemainingUpdatePath();
            (byte[] shorterPath, byte[] longerPath) = remaining.Length - node.Path.Length < 0
                ? (remaining, node.Path)
                : (node.Path, remaining);

            byte[] shorterPathValue;
            byte[] longerPathValue;

            if (Bytes.UnsafeCompare(shorterPath, node.Path))
            {
                shorterPathValue = node.Value;
                longerPathValue = context.UpdateValue;
            }
            else
            {
                shorterPathValue = context.UpdateValue;
                longerPathValue = node.Value;
            }

            int extensionLength = 0;
            for (int i = 0; i < Math.Min(shorterPath.Length, longerPath.Length) && shorterPath[i] == longerPath[i]; i++, extensionLength++)
            {
            }

            if (extensionLength == shorterPath.Length && extensionLength == longerPath.Length)
            {
                if (!context.IsUpdate)
                {
                    return node.Value;
                }

                if (context.UpdateValue == null)
                {
                    ConnectNodes(null);
                    return context.UpdateValue;
                }

                if (!Bytes.UnsafeCompare(node.Value, context.UpdateValue))
                {
                    Leaf newLeaf = new Leaf(new HexPrefix(true, remaining), context.UpdateValue);
                    newLeaf.IsDirty = true;
                    ConnectNodes(newLeaf);
                    return context.UpdateValue;
                }

                return context.UpdateValue;
            }

            if (!context.IsUpdate)
            {
                return null;
            }

            if (context.UpdateValue == null)
            {
                if (context.IgnoreMissingDelete)
                {
                    return null;
                }

                throw new InvalidOperationException($"Could not find the leaf node to delete: {Hex.FromBytes(context.UpdatePath, false)}");
            }

            if (extensionLength != 0)
            {
                byte[] extensionPath = longerPath.Slice(0, extensionLength);
                Extension extension = new Extension(new HexPrefix(false, extensionPath));
                extension.IsDirty = true;
                NodeStack.Push(new StackedNode(extension, 0));
            }

            Branch branch = new Branch();
            branch.IsDirty = true;
            if (extensionLength == shorterPath.Length)
            {
                branch.Value = shorterPathValue;
            }
            else
            {
                byte[] shortLeafPath = shorterPath.Slice(extensionLength + 1, shorterPath.Length - extensionLength - 1);
                Leaf shortLeaf = new Leaf(new HexPrefix(true, shortLeafPath), shorterPathValue);
                shortLeaf.IsDirty = true;
                branch.Nodes[shorterPath[extensionLength]] = new NodeRef(shortLeaf);
            }

            byte[] leafPath = longerPath.Slice(extensionLength + 1, longerPath.Length - extensionLength - 1);
            Leaf leaf = new Leaf(new HexPrefix(true, leafPath), longerPathValue);
            leaf.IsDirty = true;
            NodeStack.Push(new StackedNode(branch, longerPath[extensionLength]));
            ConnectNodes(leaf);

            return context.UpdateValue;
        }

        private byte[] TraverseExtension(Extension node, TraverseContext context)
        {
            byte[] remaining = context.GetRemainingUpdatePath();
            int extensionLength = 0;
            for (int i = 0; i < Math.Min(remaining.Length, node.Path.Length) && remaining[i] == node.Path[i]; i++, extensionLength++)
            {
            }

            if (extensionLength == node.Path.Length)
            {
                context.CurrentIndex += extensionLength;
                if (context.IsUpdate)
                {
                    NodeStack.Push(new StackedNode(node, 0));
                }

                node.NextNodeRef.ResolveNode(this);
                return TraverseNode(node.NextNodeRef.Node, context);
            }

            if (!context.IsUpdate)
            {
                return null;
            }

            if (context.UpdateValue == null)
            {
                if (context.IgnoreMissingDelete)
                {
                    return null;
                }

                throw new InvalidOperationException("Could find the leaf node to delete: {Hex.FromBytes(context.UpdatePath, false)}");
            }

            if (extensionLength != 0)
            {
                byte[] extensionPath = node.Path.Slice(0, extensionLength);
                Extension extension = new Extension(new HexPrefix(false, extensionPath));
                extension.IsDirty = true;
                NodeStack.Push(new StackedNode(extension, 0));
            }

            Branch branch = new Branch();
            branch.IsDirty = true;
            if (extensionLength == remaining.Length)
            {
                branch.Value = context.UpdateValue;
            }
            else
            {
                byte[] path = remaining.Slice(extensionLength + 1, remaining.Length - extensionLength - 1);
                Leaf shortLeaf = new Leaf(new HexPrefix(true, path), context.UpdateValue);
                shortLeaf.IsDirty = true;
                branch.Nodes[remaining[extensionLength]] = new NodeRef(shortLeaf);
            }

            if (node.Path.Length - extensionLength > 1)
            {
                byte[] extensionPath = node.Path.Slice(extensionLength + 1, node.Path.Length - extensionLength - 1);
                Extension secondExtension = new Extension(new HexPrefix(false, extensionPath), node.NextNodeRef);
                secondExtension.IsDirty = true;
                branch.Nodes[node.Path[extensionLength]] = new NodeRef(secondExtension);
            }
            else
            {
                branch.Nodes[node.Path[extensionLength]] = node.NextNodeRef;
            }

            ConnectNodes(branch);
            return context.UpdateValue;
        }

        private struct TraverseContext
        {
            public byte[] UpdatePath { get; }
            public byte[] UpdateValue { get; }
            public bool IsUpdate { get; }
            public bool IgnoreMissingDelete { get; }
            public int CurrentIndex { get; set; }
            public int RemainingUpdatePathLength => UpdatePath.Length - CurrentIndex;

            public byte[] GetRemainingUpdatePath()
            {
                return UpdatePath.Slice(CurrentIndex, RemainingUpdatePathLength);
            }

            public TraverseContext(byte[] updatePath, byte[] updateValue, bool isUpdate, bool ignoreMissingDelete = true)
            {
                UpdatePath = updatePath;
                UpdateValue = updateValue;
                IsUpdate = isUpdate;
                IgnoreMissingDelete = ignoreMissingDelete;
                CurrentIndex = 0;
            }
        }

        private struct StackedNode
        {
            public StackedNode(Node node, int pathIndex)
            {
                Node = node;
                PathIndex = pathIndex;
            }

            public Node Node { get; }
            public int PathIndex { get; }
        }
    }
}