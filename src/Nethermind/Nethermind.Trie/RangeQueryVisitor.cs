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
    private bool _shouldVisit = true;
    private Dictionary<byte[], byte[]> _collectedNodes = new();
    private long _currentBytesCount = 0;
    private byte[] _startHash;
    private byte[] _limitHash;
    private int _nodeLimit = 1000;
    private bool _checkStartRange = true;
    private HashSet<Keccak> _nodeToVisitFilter = new();
    private long _byteLimit;
    private bool IsAccountVisitor;
    private readonly AccountDecoder _decoder = new(true);

    public RangeQueryVisitor(byte[] startHash, byte[] limitHash, bool isAccountVisitor, long byteLimit=-1, int nodeLimit = 1000)
    {
        _startHash = new byte[64];
        Nibbles.BytesToNibbleBytes(startHash, _startHash);

        _limitHash = new byte[64];
        Nibbles.BytesToNibbleBytes(limitHash, _limitHash);

        IsAccountVisitor = isAccountVisitor;
        _nodeLimit = nodeLimit;
        _byteLimit = byteLimit;
    }

    private static int ComparePath(IReadOnlyList<byte> path, byte[] hash)
    {
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

    private bool ShouldVisit(IReadOnlyList<byte> path)
    {
        if (_collectedNodes.Count >= _nodeLimit || (_byteLimit != -1 && _currentBytesCount >= _byteLimit))
        {
            return false;
        }

        int compResult = ComparePath(path, _limitHash);
        return compResult != -1;
    }
    public bool ShouldVisit(Keccak nextNode)
    {
        return _checkStartRange ? _nodeToVisitFilter.Contains(nextNode) : _shouldVisit;
    }

    public Dictionary<byte[], byte[]> GetNodes()
    {
        return _collectedNodes;
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
            if (compRes == 1)
            {
                // if path < _startHash[:path.Count] - return and check for the next node.
                return;
            }
            if (compRes == 0)
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
            if (compRes == -1)
            {
                // if path > _startHash[:path.Count] -> found the first element after the start range.
                // continue visiting and collecting next nodes and set _checkStartRange = false
                _checkStartRange = false;
            }
        }

        bool shouldVisitNode = ShouldVisit(path);
        if (!shouldVisitNode)
        {
            _shouldVisit = false;
            return;
        }


    }

    public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext)
    {
        List<byte>? path = trieVisitContext.AbsolutePathNibbles;
        if (_checkStartRange)
        {
            _nodeToVisitFilter.Remove(node.Keccak);
            int compRes = ComparePath(path, _startHash);
            if (compRes == 1)
            {
                // if path < _startHash[:path.Count] - return and check for the next node.
                return;
            }
            if (compRes == 0)
            {
                // this is a important case - here the path == _startHash[:path.Count]
                // the child should be visited
                _nodeToVisitFilter.Add(node.GetChildHash(0));
                return;
            }
            if (compRes == -1)
            {
                // if path > _startHash[:path.Count] -> found the first element after the start range.
                // continue visiting and collecting next nodes and set _checkStartRange = false
                _checkStartRange = false;
            }
        }

        bool shouldVisitNode = ShouldVisit(trieVisitContext.AbsolutePathNibbles);
        if (!shouldVisitNode)
        {
            _shouldVisit = false;
            return;
        }
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

        byte[]? nodeValue = IsAccountVisitor ? ConvertFullToSlimAccount(node.Value) : node.Value;
        _collectedNodes[Nibbles.ToBytes(path.ToArray())] = nodeValue;
        _currentBytesCount += 32 + nodeValue!.Length;
    }

    public void VisitCode(Keccak codeHash, TrieVisitContext trieVisitContext)
    {
        throw new System.NotImplementedException();
    }

    private byte[]? ConvertFullToSlimAccount(byte[]? accountRlp)
    {
        if (accountRlp is null)
        {
            return null;
        }
        return _decoder.Encode(_decoder.Decode(new RlpStream(accountRlp))).Bytes;
    }
}
