// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Trie;
using Nethermind.Verkle.Tree.Interfaces;
using Nethermind.Verkle.Tree.Nodes;

namespace Nethermind.Verkle.Tree;

public class VerkleRangeProofCollector: IVerkleTreeVisitor
{
    private readonly byte[]? _startStem;
    private bool _findFirstNodeInRange = true;

    private readonly byte[]? _limitStem;
    private readonly bool _comparePathWithLimitHash = true;

    private bool _shouldContinueTraversing = true;

    private long _currentBytesCount;
    private readonly Dictionary<byte[], byte[]> _collectedNodes = new();

    private HashSet<byte[]>? _nodeToVisitFilterInstance;

    private HashSet<byte[]> NodeToVisitFilter => _nodeToVisitFilterInstance ??
                                                 LazyInitializer.EnsureInitialized(ref _nodeToVisitFilterInstance,
                                                     () => new HashSet<byte[]>());

    private readonly int _nodeLimit;
    private readonly long _byteLimit;

    private readonly long _hardByteLimit;
    public bool _isStoppedDueToHardLimit;

    public VerkleRangeProofCollector(byte[] startStem, byte[] limitStem, bool isAccountVisitor, long byteLimit = -1, long hardByteLimit = 200000, int nodeLimit = 10000)
    {
        if (Bytes.AreEqual(startStem, Keccak.Zero.Bytes[..31]))
        {
            _findFirstNodeInRange = false;
        }
        else
        {
            _startStem = ArrayPool<byte>.Shared.Rent(64);
            Nibbles.BytesToNibbleBytes(limitStem, _startStem);
        }

        if (Bytes.AreEqual(limitStem, Keccak.MaxValue.Bytes[..31]))
        {
            _comparePathWithLimitHash = false;
        }
        else
        {
            _limitStem = ArrayPool<byte>.Shared.Rent(64);
            Nibbles.BytesToNibbleBytes(limitStem, _limitStem);
        }

        _nodeLimit = nodeLimit;
        _byteLimit = byteLimit;
        _hardByteLimit = hardByteLimit;
    }

    private static int ComparePath(List<byte> path, byte[]? hash)
    {
        Span<byte> pathSpan = CollectionsMarshal.AsSpan(path);
        // compare the `path` and `hash` to check if a key with prefix `path` would come after the hash or not
        return Bytes.Comparer.CompareGreaterThan(hash.AsSpan()[..pathSpan.Length], pathSpan);
    }

    public bool ShouldVisit(byte[] nextNode)
    {
        // if still looking for node just after the startHash, then only visit node that are present in _nodeToVisitFilter
        return _findFirstNodeInRange ? NodeToVisitFilter.Contains(nextNode) : _shouldContinueTraversing;
    }

    private bool ShouldVisit(List<byte> path)
    {
        if (_collectedNodes.Count >= _nodeLimit)
        {
            _isStoppedDueToHardLimit = true;
            return false;
        }

        if (_byteLimit != -1 && _currentBytesCount >= _byteLimit)
        {
            return false;
        }

        if (_currentBytesCount >= _hardByteLimit)
        {
            _isStoppedDueToHardLimit = true;
            return false;
        }

        if (!_comparePathWithLimitHash) return true;
        int compResult = ComparePath(path, _limitStem);
        return compResult != -1;
    }

    public void VisitTree(VerkleCommitment rootHash, TrieVisitContext trieVisitContext) { }

    public void VisitMissingNode(byte[] nodeKey, TrieVisitContext trieVisitContext) { }

    public void VisitBranchNode(InternalNode node, TrieVisitContext trieVisitContext)
    {
        List<byte> path = trieVisitContext.AbsolutePathIndex;

        if (_findFirstNodeInRange)
        {
            NodeToVisitFilter.Remove(path.ToArray());

            int compRes = ComparePath(path, _startStem);
            switch (compRes)
            {
                case 1:
                    // if path < _startHash[:path.Count] - return and check for the next node.
                    return;
                case 0:
                {
                    // this is a important case - here the path == _startHash[:path.Count]
                    // the index of child should be _startHash[path.Count]
                    byte index = _startStem[path.Count];
                    for (int i = index; i < 256; i++)
                    {
                        using (trieVisitContext.AbsolutePathNext((byte)i))
                        {
                            NodeToVisitFilter.Add(trieVisitContext.AbsolutePathIndex.ToArray());
                        }
                    }
                    return;
                }
                case -1:
                    // if path > _startHash[:path.Count] -> found the first element after the start range.
                    // continue visiting and collecting next nodes and set _findFirstNodeInRange = false
                    _findFirstNodeInRange = false;
                    break;
            }
        }

        bool shouldVisitNode = ShouldVisit(path);
        if (shouldVisitNode) return;
        _shouldContinueTraversing = false;
    }

    public void VisitStemNode(InternalNode node, TrieVisitContext trieVisitContext)
    {
        throw new NotImplementedException();
    }

    public void VisitLeafNode(ReadOnlySpan<byte> nodeKey, TrieVisitContext trieVisitContext, byte[]? nodeValue)
    {
        throw new NotImplementedException();
    }
}
