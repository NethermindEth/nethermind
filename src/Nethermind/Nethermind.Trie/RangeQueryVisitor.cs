//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Trie;

public class RangeQueryVisitor : ITreeVisitor, IDisposable
{

    private readonly byte[]? _startHash;
    private bool _findFirstNodeInRange = true;

    private readonly byte[]? _limitHash;
    private readonly bool _comparePathWithLimitHash = true;

    private readonly bool _isAccountVisitor;
    private bool _shouldContinueTraversing = true;

    private long _currentBytesCount;
    private readonly Dictionary<byte[], byte[]> _collectedNodes = new();

    private HashSet<Keccak>? _nodeToVisitFilterInstance;

    private HashSet<Keccak> NodeToVisitFilter => _nodeToVisitFilterInstance ??
                                                  LazyInitializer.EnsureInitialized(ref _nodeToVisitFilterInstance,
                                                      () => new HashSet<Keccak>());

    private readonly int _nodeLimit;
    private readonly long _byteLimit;

    private readonly long _hardByteLimit;
    public bool _isStoppedDueToHardLimit;

    private readonly AccountDecoder _decoder = new(true);

    public RangeQueryVisitor(byte[] startHash, byte[] limitHash, bool isAccountVisitor, long byteLimit = -1, long hardByteLimit = 200000, int nodeLimit = 10000)
    {

        if (Bytes.AreEqual(startHash, Keccak.Zero.Bytes))
        {
            _findFirstNodeInRange = false;
        }
        else
        {
            _startHash = ArrayPool<byte>.Shared.Rent(64);
            Nibbles.BytesToNibbleBytes(startHash, _startHash);
        }

        if (Bytes.AreEqual(limitHash, Keccak.MaxValue.Bytes))
        {
            _comparePathWithLimitHash = false;
        }
        else
        {
            _limitHash = ArrayPool<byte>.Shared.Rent(64);
            Nibbles.BytesToNibbleBytes(limitHash, _limitHash);
        }

        _isAccountVisitor = isAccountVisitor;
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

    // to check if the node should be visited on the based of its path and limitHash
    private bool ShouldVisit(List<byte> path)
    {
        if (_collectedNodes.Count >= _nodeLimit)
        {
            _isStoppedDueToHardLimit = true;
            return false;
        }

        if (_isAccountVisitor && (_byteLimit != -1 && _currentBytesCount >= _byteLimit))
        {
            return false;
        }

        if (_currentBytesCount >= _hardByteLimit)
        {
            _isStoppedDueToHardLimit = true;
            return false;
        }

        if (!_comparePathWithLimitHash) return true;
        int compResult = ComparePath(path, _limitHash);
        return compResult != -1;
    }

    public bool ShouldVisit(Keccak nextNode)
    {
        // if still looking for node just after the startHash, then only visit node that are present in _nodeToVisitFilter
        return _findFirstNodeInRange ? NodeToVisitFilter.Contains(nextNode) : _shouldContinueTraversing;
    }

    public (Dictionary<byte[], byte[]>, long) GetNodesAndSize()
    {
        return (_collectedNodes, _currentBytesCount);
    }

    public void VisitTree(Keccak rootHash, TrieVisitContext trieVisitContext)
    {
    }

    public void VisitMissingNode(Keccak nodeHash, TrieVisitContext trieVisitContext)
    {
    }

    public void VisitBranch(TrieNode node, TrieVisitContext trieVisitContext)
    {
        List<byte> path = trieVisitContext.AbsolutePathNibbles;

        if (_findFirstNodeInRange)
        {
            NodeToVisitFilter.Remove(node.Keccak);

            int compRes = ComparePath(path, _startHash);
            switch (compRes)
            {
                case 1:
                    // if path < _startHash[:path.Count] - return and check for the next node.
                    return;
                case 0:
                    {
                        // this is a important case - here the path == _startHash[:path.Count]
                        // the index of child should be _startHash[path.Count]
                        byte index = _startHash[path.Count];
                        for (int i = index; i < TrieNode.BranchesCount; i++)
                        {
                            NodeToVisitFilter.Add(node.GetChildHash(i));
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

    public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext)
    {
        List<byte>? path = trieVisitContext.AbsolutePathNibbles;

        if (_findFirstNodeInRange)
        {
            NodeToVisitFilter.Remove(node.Keccak);
            int compRes = ComparePath(path, _startHash);
            switch (compRes)
            {
                case 1:
                    // if path < _startHash[:path.Count] - return and check for the next node.
                    return;
                case 0:
                    // this is a important case - here the path == _startHash[:path.Count]
                    // the child should be visited
                    NodeToVisitFilter.Add(node.GetChildHash(0));
                    return;
                case -1:
                    // if path > _startHash[:path.Count] -> found the first element after the start range.
                    // continue visiting and collecting next nodes and set _findFirstNodeInRange = false
                    _findFirstNodeInRange = false;
                    break;
            }
        }

        bool shouldVisitNode = ShouldVisit(trieVisitContext.AbsolutePathNibbles);
        if (shouldVisitNode) return;
        _shouldContinueTraversing = false;
    }

    public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, byte[] value = null)
    {
        List<byte>? path = trieVisitContext.AbsolutePathNibbles;

        if (_findFirstNodeInRange)
        {
            NodeToVisitFilter.Remove(node.Keccak);

            int compRes = ComparePath(path, _startHash);
            if (compRes == 1)
            {
                // if path < _startHash[:path.Count] - return
                return;
            }
            // if path >= _startHash[:path.Count] -> found the first element after the start range.
            // continue to _collect this node and all the other nodes till _limitHash
            _findFirstNodeInRange = false;
        }

        bool shouldVisitNode = ShouldVisit(trieVisitContext.AbsolutePathNibbles);
        if (!shouldVisitNode)
        {
            _shouldContinueTraversing = false;
            return;
        }

        CollectNode(path, node.Value);
    }

    public void VisitCode(Keccak codeHash, TrieVisitContext trieVisitContext)
    {
        throw new NotImplementedException();
    }

    private byte[]? ConvertFullToSlimAccount(byte[]? accountRlp)
    {
        return accountRlp is null ? null : _decoder.Encode(_decoder.Decode(new RlpStream(accountRlp))).Bytes;
    }

    private void CollectNode(List<byte> path, byte[]? value)
    {
        byte[]? nodeValue = _isAccountVisitor ? ConvertFullToSlimAccount(value) : value;
        _collectedNodes[Nibbles.ToBytes(path)] = nodeValue;
        _currentBytesCount += 32 + nodeValue!.Length;
    }

    public void Dispose()
    {
        if (_startHash != null) ArrayPool<byte>.Shared.Return(_startHash);
        if (_limitHash != null) ArrayPool<byte>.Shared.Return(_limitHash);
    }
}
