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
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Trie;

public class RangeQueryVisitor : ITreeVisitor<TreePathContext>, IDisposable
{
    private bool _skipStarthashComparison = false;
    private ValueHash256? _startHash;
    private TreePath[] _truncatedStartHashes;
    private readonly ValueHash256? _limitHash;

    private readonly bool _isAccountVisitor;

    private long _currentBytesCount;
    private readonly Dictionary<ValueHash256, byte[]> _collectedNodes = new();

    // For determining proofs
    private TreePath? _leftmostLeafPath;
    private TreePath? _rightmostLeafPath;
    private (TreePath, TrieNode)?[] _leftmostNodes = new (TreePath, TrieNode)?[65];
    private (TreePath, TrieNode)?[] _rightmostNodes = new (TreePath, TrieNode)?[65];

    private readonly int _nodeLimit;
    private readonly long _byteLimit;
    private readonly long _hardByteLimit;

    public bool StoppedEarly { get; set; } = false;
    public bool IsFullDbScan => false;
    private readonly AccountDecoder _standardDecoder = new AccountDecoder();
    private readonly AccountDecoder _slimDecoder = new AccountDecoder(slimFormat: true);
    private readonly CancellationToken _cancellationToken;


    public ReadFlags ExtraReadFlag => ReadFlags.HintReadAhead;
    public RangeQueryVisitor(in ValueHash256 startHash, in ValueHash256 limitHash, bool isAccountVisitor, long byteLimit = -1, long hardByteLimit = 200000, int nodeLimit = 10000, CancellationToken cancellationToken = default)
    {
        if (startHash != ValueKeccak.Zero)
        {
            _startHash = startHash;
        }

        if (limitHash != ValueKeccak.MaxValue)
        {
            _limitHash = limitHash;
        }

        _cancellationToken = cancellationToken;
        _isAccountVisitor = isAccountVisitor;
        _nodeLimit = nodeLimit;
        _byteLimit = byteLimit;
        _hardByteLimit = hardByteLimit;

        TreePath startHashPath = new TreePath(startHash, 64);
        _truncatedStartHashes = new TreePath[65];
        for (int i = 0; i < 65; i++)
        {
            _truncatedStartHashes[i] = startHashPath.Truncate(i);
        }
    }

    // to check if the node should be visited on the based of its path and limitHash
    private bool ShouldVisit(in TreePath path)
    {
        if (_cancellationToken.IsCancellationRequested)
        {
            StoppedEarly = true;
            return false;
        }

        if (_collectedNodes.Count >= _nodeLimit)
        {
            StoppedEarly = true;
            return false;
        }

        if (_byteLimit != -1 && _currentBytesCount >= _byteLimit)
        {
            StoppedEarly = true;
            return false;
        }

        if (_currentBytesCount >= _hardByteLimit)
        {
            StoppedEarly = true;
            return false;
        }

        int compResult = 0;
        if (!_skipStarthashComparison && _startHash != null)
        {
            compResult = path.CompareTo(_truncatedStartHashes[path.Length]);
            if (compResult < 0)
            {
                return false;
            }
        }

        if (_limitHash != null)
        {
            compResult = path.Path.CompareTo(_limitHash.Value);
            if (compResult > 0)
            {
                StoppedEarly = true;
                return false;
            }
        }

        return true;
    }

    public bool ShouldVisit(in TreePathContext ctx, Hash256 nextNode)
    {
        return ShouldVisit(ctx.Path);
    }


    public (Dictionary<ValueHash256, byte[]>, long) GetNodesAndSize()
    {
        return (_collectedNodes, _currentBytesCount);
    }

    public byte[][] GetProofs()
    {
        if (_leftmostLeafPath == null) return Array.Empty<byte[]>();

        HashSet<byte[]> proofs = new();
        if (_startHash != Keccak.Zero)
        {
            TreePath leftmostPath = _leftmostLeafPath.Value;
            for (int i = 64; i >= 0; i--)
            {
                if (!_leftmostNodes[i].HasValue) continue;

                (TreePath path, TrieNode node) = _leftmostNodes[i].Value;
                leftmostPath.TruncateMut(i);
                if (leftmostPath != path) continue;

                proofs.Add(node.FullRlp.ToArray());
            }
        }

        TreePath rightmostPath = _rightmostLeafPath.Value;
        for (int i = 64; i >= 0; i--)
        {
            if (!_rightmostNodes[i].HasValue) continue;

            (TreePath path, TrieNode node) = _rightmostNodes[i].Value;
            rightmostPath.TruncateMut(i);
            if (rightmostPath != path) continue;

            proofs.Add(node.FullRlp.ToArray());
        }

        return proofs.ToArray();
    }

    public void VisitTree(in TreePathContext nodeContext, Hash256 rootHash, TrieVisitContext trieVisitContext)
    {
    }


    public void VisitMissingNode(in TreePathContext ctx, Hash256 nodeHash, TrieVisitContext trieVisitContext)
    {
    }

    public void VisitBranch(in TreePathContext ctx, TrieNode node, TrieVisitContext trieVisitContext)
    {
        if (!_leftmostNodes[ctx.Path.Length].HasValue) _leftmostNodes[ctx.Path.Length] = (ctx.Path, node);
        _rightmostNodes[ctx.Path.Length] = (ctx.Path, node);
    }

    public void VisitExtension(in TreePathContext ctx, TrieNode node, TrieVisitContext trieVisitContext)
    {
        if (!_leftmostNodes[ctx.Path.Length].HasValue) _leftmostNodes[ctx.Path.Length] = (ctx.Path, node);
        _rightmostNodes[ctx.Path.Length] = (ctx.Path, node);
    }

    public void VisitLeaf(in TreePathContext ctx, TrieNode node, TrieVisitContext trieVisitContext, ReadOnlySpan<byte> value)
    {
        TreePath path = ctx.Path.Append(node.Key);
        if (!ShouldVisit(path)) return;
        CollectNode(path, node.Value);
        // We found at least one leaf, don't compare with startHash anymore
        if (_startHash != null)
        {
            _skipStarthashComparison = true;
        }
    }

    public void VisitCode(in TreePathContext nodeContext, Hash256 codeHash, TrieVisitContext trieVisitContext)
    {
    }

    public void VisitAccount(in TreePathContext path, TrieNode node, TrieVisitContext trieVisitContext, Account account)
    {
    }

    private byte[]? ConvertFullToSlimAccount(CappedArray<byte> accountRlp)
    {
        return accountRlp.IsNull ? null : _slimDecoder.Encode(_standardDecoder.Decode(new RlpStream(accountRlp))).Bytes;
    }

    private void CollectNode(TreePath path, CappedArray<byte> value)
    {
        if (_leftmostLeafPath == null)
        {
            _leftmostLeafPath = path;
        }
        _rightmostLeafPath = path;

        byte[]? nodeValue = _isAccountVisitor ? ConvertFullToSlimAccount(value) : value.ToArray();
        _collectedNodes[path.Path] = nodeValue;
        _currentBytesCount += 32 + nodeValue!.Length;
    }

    public void Dispose()
    {
    }
}
