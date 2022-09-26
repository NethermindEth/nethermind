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

namespace Nethermind.Trie;

public class RangeQueryVisitor: ITreeVisitor
{
    private bool _shouldVisit = true;
    private List<byte[]> _collectedNodes = new();
    private byte[] _limitHash;
    private int _nodeLimit = 1000;

    public RangeQueryVisitor(byte[] limitHash, int nodeLimit = 1000)
    {
        _limitHash = new byte[64];
        Nibbles.BytesToNibbleBytes(limitHash, _limitHash);
        Console.WriteLine("Nibbles Limit Hash");
        Console.WriteLine(String.Join(", ", _limitHash));
        _nodeLimit = nodeLimit;
    }

    private bool ShouldVisit(IReadOnlyList<byte> path)
    {
        Console.WriteLine("Path:");
        Console.WriteLine(String.Join(", ", path.ToArray()));
        int nodeCount = path.Count;
        if (nodeCount >= _nodeLimit)
        {
            return false;
        }
        for (int i = 0; i < nodeCount; i++)
        {
            if (_limitHash[i] > path[i])
            {
                Console.WriteLine("true");
                return true;
            }
            if (_limitHash[i] < path[i])
            {
                Console.WriteLine("true");
                return false;
            }
        }
        // equality case
        Console.WriteLine("true");
        return true;
    }
    public bool ShouldVisit(Keccak nextNode)
    {
        return _shouldVisit;
    }

    public byte[][] GetNodes()
    {
        return _collectedNodes.ToArray();
    }

    public void VisitTree(Keccak rootHash, TrieVisitContext trieVisitContext)
    {
    }

    public void VisitMissingNode(Keccak nodeHash, TrieVisitContext trieVisitContext)
    {
    }

    public void VisitBranch(TrieNode node, TrieVisitContext trieVisitContext)
    {
        bool shouldVisitNode = ShouldVisit(trieVisitContext.AbsolutePathNibbles);
        if (!shouldVisitNode)
        {
            _shouldVisit = false;
            return;
        }


    }

    public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext)
    {
        bool shouldVisitNode = ShouldVisit(trieVisitContext.AbsolutePathNibbles);
        if (!shouldVisitNode)
        {
            _shouldVisit = false;
            return;
        }
    }

    public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, byte[] value = null)
    {
        bool shouldVisitNode = ShouldVisit(trieVisitContext.AbsolutePathNibbles);
        if (!shouldVisitNode)
        {
            _shouldVisit = false;
            return;
        }

        _collectedNodes.Add(node.FullRlp);
    }

    public void VisitCode(Keccak codeHash, TrieVisitContext trieVisitContext)
    {
        throw new System.NotImplementedException();
    }
}
