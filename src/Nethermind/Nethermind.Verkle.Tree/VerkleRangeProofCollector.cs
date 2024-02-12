// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Trie;
using Nethermind.Verkle.Tree.TreeNodes;

namespace Nethermind.Verkle.Tree;

public class VerkleRangeProofCollector : IVerkleTreeVisitor
{
    private readonly long _byteLimit;
    private readonly Dictionary<byte[], byte[]> _collectedNodes = new();
    private readonly bool _comparePathWithLimitHash = true;

    private readonly long _hardByteLimit;

    private readonly byte[]? _limitStem;

    private readonly int _nodeLimit;
    private readonly byte[]? _startStem;

    private readonly long _currentBytesCount;
    private bool _findFirstNodeInRange = true;
    public bool _isStoppedDueToHardLimit;

    private HashSet<byte[]>? _nodeToVisitFilterInstance;

    private bool _shouldContinueTraversing = true;

    public VerkleRangeProofCollector(byte[] startStem, byte[] limitStem, bool isAccountVisitor, long byteLimit = -1,
        long hardByteLimit = 200000, int nodeLimit = 10000)
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
        _currentBytesCount = 0;
    }

    private HashSet<byte[]> NodeToVisitFilter => _nodeToVisitFilterInstance ??
                                                 LazyInitializer.EnsureInitialized(ref _nodeToVisitFilterInstance,
                                                     () => new HashSet<byte[]>());

    public bool ShouldVisit(byte[] nextNode)
    {
        // if still looking for node just after the startHash, then only visit node that are present in _nodeToVisitFilter
        return _findFirstNodeInRange ? NodeToVisitFilter.Contains(nextNode) : _shouldContinueTraversing;
    }

    public void VisitTree(Hash256 rootHash, TrieVisitContext trieVisitContext)
    {
    }

    public void VisitMissingNode(byte[] nodeKey, TrieVisitContext trieVisitContext)
    {
    }

    public void VisitBranchNode(InternalNode node, TrieVisitContext trieVisitContext)
    {
        List<byte> path = trieVisitContext.AbsolutePathIndex;

        if (_findFirstNodeInRange)
        {
            NodeToVisitFilter.Remove(path.ToArray());

            var compRes = ComparePath(path, _startStem);
            switch (compRes)
            {
                case 1:
                    // if path < _startHash[:path.Count] - return and check for the next node.
                    return;
                case 0:
                {
                    // this is a important case - here the path == _startHash[:path.Count]
                    // the index of child should be _startHash[path.Count]
                    var index = _startStem[path.Count];
                    for (int i = index; i < 256; i++)
                        using (trieVisitContext.AbsolutePathNext((byte)i))
                        {
                            NodeToVisitFilter.Add(trieVisitContext.AbsolutePathIndex.ToArray());
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

        var shouldVisitNode = ShouldVisit(path);
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

    private static int ComparePath(List<byte> path, byte[]? hash)
    {
        Span<byte> pathSpan = CollectionsMarshal.AsSpan(path);
        // compare the `path` and `hash` to check if a key with prefix `path` would come after the hash or not
        return Bytes.Comparer.CompareGreaterThan(hash.AsSpan()[..pathSpan.Length], pathSpan);
    }

    private bool ShouldVisit(List<byte> path)
    {
        if (_collectedNodes.Count >= _nodeLimit)
        {
            _isStoppedDueToHardLimit = true;
            return false;
        }

        if (_byteLimit != -1 && _currentBytesCount >= _byteLimit) return false;

        if (_currentBytesCount >= _hardByteLimit)
        {
            _isStoppedDueToHardLimit = true;
            return false;
        }

        if (!_comparePathWithLimitHash) return true;
        var compResult = ComparePath(path, _limitStem);
        return compResult != -1;
    }
}
