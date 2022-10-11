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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Trie;

public class RangeQueryVisitor: ITreeVisitor
{

    private readonly byte[] _startHash;
    private bool _checkStartRange = true;

    private readonly byte[] _limitHash;
    private readonly bool _checkEndRange = true;

    private readonly bool _isAccountVisitor;
    private bool _shouldVisit = true;

    private long _currentBytesCount = 0;
    private readonly Dictionary<byte[], byte[]> _collectedNodes = new();
    private readonly HashSet<Keccak> _nodeToVisitFilter = new();

    private readonly int _nodeLimit;
    private readonly long _byteLimit;

    private readonly long _hardByteLimit;
    public bool _isStoppedDueToHardLimit = false;

    private readonly AccountDecoder _decoder = new(true);

    public RangeQueryVisitor(byte[] startHash, byte[] limitHash, bool isAccountVisitor, long byteLimit=-1, long hardByteLimit = 200000, int nodeLimit = 10000)
    {

        if (startHash.SequenceEqual(Keccak.Zero.Bytes))
        {
            _checkStartRange = false;
        }
        else
        {
            _startHash = new byte[64];
            Nibbles.BytesToNibbleBytes(startHash, _startHash);
        }

        if (startHash.SequenceEqual(Keccak.MaxValue.Bytes))
        {
            _checkEndRange = false;
        }
        else
        {
            _limitHash = new byte[64];
            Nibbles.BytesToNibbleBytes(limitHash, _limitHash);
        }

        _isAccountVisitor = isAccountVisitor;
        _nodeLimit = nodeLimit;
        _byteLimit = byteLimit;
        _hardByteLimit = hardByteLimit;
    }

    private static int ComparePath(IReadOnlyList<byte> path, byte[] hash)
    {
        // compare the `path` and `hash` to check if a key with prefix `path` would come after the hash or not
        for (int i = 0; i < path.Count; i++)
        {
            if (hash[i] > path[i])
            {
                return 1;
            }
            if (hash[i] < path[i])
            {
                return -1;
            }
        }
        return 0;
    }

    // to check if the node should be visited on the based of its path and limitHash
    private bool ShouldVisit(IReadOnlyList<byte> path)
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

        if (!_checkEndRange) return true;
        int compResult = ComparePath(path, _limitHash);
        return compResult != -1;
    }

    public bool ShouldVisit(Keccak nextNode)
    {
        // if still looking for node just after the startHash, then only visit node that are present in _nodeToVisitFilter
        return _checkStartRange ? _nodeToVisitFilter.Contains(nextNode) : _shouldVisit;
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
        List<byte>? path = trieVisitContext.AbsolutePathNibbles;

        if (_checkStartRange)
        {
            _nodeToVisitFilter.Remove(node.Keccak);

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
                        _nodeToVisitFilter.Add(node.GetChildHash(i));
                    }
                    return;
                }
                case -1:
                    // if path > _startHash[:path.Count] -> found the first element after the start range.
                    // continue visiting and collecting next nodes and set _checkStartRange = false
                    _checkStartRange = false;
                    break;
            }
        }

        bool shouldVisitNode = ShouldVisit(path);
        if (shouldVisitNode) return;
        _shouldVisit = false;

    }

    public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext)
    {
        List<byte>? path = trieVisitContext.AbsolutePathNibbles;

        if (_checkStartRange)
        {
            _nodeToVisitFilter.Remove(node.Keccak);
            int compRes = ComparePath(path, _startHash);
            switch (compRes)
            {
                case 1:
                    // if path < _startHash[:path.Count] - return and check for the next node.
                    return;
                case 0:
                    // this is a important case - here the path == _startHash[:path.Count]
                    // the child should be visited
                    _nodeToVisitFilter.Add(node.GetChildHash(0));
                    return;
                case -1:
                    // if path > _startHash[:path.Count] -> found the first element after the start range.
                    // continue visiting and collecting next nodes and set _checkStartRange = false
                    _checkStartRange = false;
                    break;
            }
        }

        bool shouldVisitNode = ShouldVisit(trieVisitContext.AbsolutePathNibbles);
        if (shouldVisitNode) return;
        _shouldVisit = false;
    }

    public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, byte[] value = null)
    {
        List<byte>? path = trieVisitContext.AbsolutePathNibbles;

        if (_checkStartRange)
        {
            _nodeToVisitFilter.Remove(node.Keccak);

            int compRes = ComparePath(path, _startHash);
            if (compRes == 1)
            {
                // if path < _startHash[:path.Count] - return
                return;
            }
            // if path >= _startHash[:path.Count] -> found the first element after the start range.
            // continue to _collect this node and all the other nodes till _limitHash
            _checkStartRange = false;
        }

        bool shouldVisitNode = ShouldVisit(trieVisitContext.AbsolutePathNibbles);
        if (!shouldVisitNode)
        {
            _shouldVisit = false;
            return;
        }

        CollectNode(path.ToArray(), node.Value);
    }

    public void VisitCode(Keccak codeHash, TrieVisitContext trieVisitContext)
    {
        throw new NotImplementedException();
    }

    private byte[]? ConvertFullToSlimAccount(byte[]? accountRlp)
    {
        return accountRlp is null ? null : _decoder.Encode(_decoder.Decode(new RlpStream(accountRlp))).Bytes;
    }

    private void CollectNode(byte[] path, byte[]? value)
    {
        byte[]? nodeValue = _isAccountVisitor ? ConvertFullToSlimAccount(value) : value;
        _collectedNodes[Nibbles.ToBytes(path)] = nodeValue;
        _currentBytesCount += 32 + nodeValue!.Length;
    }
}
