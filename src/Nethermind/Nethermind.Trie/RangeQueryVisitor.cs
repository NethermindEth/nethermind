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
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Trie;

public class RangeQueryVisitor : ITreeVisitor<TreePathContext>, IDisposable
{
    private bool _skipStarthashComparison = false;
    private TreePath _startHash;
    private readonly ValueHash256 _limitHash;

    private readonly bool _isAccountVisitor;

    private long _currentBytesCount;
    private bool _lastNodeFound = false;
    private readonly Dictionary<ValueHash256, byte[]> _collectedNodes = new();

    // For determining proofs
    private (TreePath, TrieNode)?[] _leftmostNodes = new (TreePath, TrieNode)?[65];
    private (TreePath, TrieNode)?[] _rightmostNodes = new (TreePath, TrieNode)?[65];
    private (TreePath, TrieNode)? _leftLeafProof = null;
    private TreePath _rightmostLeafPath;

    private readonly int _nodeLimit;
    private readonly long _byteLimit;

    public bool StoppedEarly { get; set; } = false;
    public bool IsFullDbScan => false;
    private readonly AccountDecoder _standardDecoder = new AccountDecoder();
    private readonly AccountDecoder _slimDecoder = new AccountDecoder(slimFormat: true);
    private readonly CancellationToken _cancellationToken;

    public ReadFlags ExtraReadFlag { get; }

    public RangeQueryVisitor(
        in ValueHash256 startHash,
        in ValueHash256 limitHash,
        bool isAccountVisitor,
        long byteLimit = 200000,
        int nodeLimit = 10000,
        ReadFlags readFlags = ReadFlags.None,
        CancellationToken cancellationToken = default)
    {
        _limitHash = limitHash;
        if (startHash == ValueKeccak.Zero)
            _skipStarthashComparison = true;
        else
            _startHash = new TreePath(startHash, 64);

        _cancellationToken = cancellationToken;
        _isAccountVisitor = isAccountVisitor;
        _nodeLimit = nodeLimit;
        _byteLimit = byteLimit;
        ExtraReadFlag = readFlags;
    }

    private bool ShouldVisit(in TreePath path)
    {
        if (_cancellationToken.IsCancellationRequested)
        {
            StoppedEarly = true;
            return false;
        }

        if (_lastNodeFound)
        {
            StoppedEarly = true;
            return false;
        }

        if (_collectedNodes.Count >= _nodeLimit)
        {
            StoppedEarly = true;
            return false;
        }

        if (_currentBytesCount >= _byteLimit)
        {
            StoppedEarly = true;
            return false;
        }

        if (!_skipStarthashComparison)
        {
            if (_startHash.CompareToTruncated(path, path.Length) > 0)
            {
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

    public ArrayPoolList<byte[]> GetProofs()
    {
        if (_leftLeafProof is null) return ArrayPoolList<byte[]>.Empty();

        HashSet<byte[]> proofs = new();
        // Note: although nethermind works just fine without left proof if start with zero starting hash,
        // its out of spec.
        (TreePath leftmostPath, TrieNode leftmostLeafProof) = _leftLeafProof.Value;
        proofs.Add(leftmostLeafProof.FullRlp.ToArray());

        for (int i = 64; i >= 0; i--)
        {
            if (!_leftmostNodes[i].HasValue) continue;

            (TreePath path, TrieNode node) = _leftmostNodes[i].Value;
            leftmostPath.TruncateMut(i);
            if (leftmostPath != path) continue;

            proofs.Add(node.FullRlp.ToArray());
        }

        TreePath rightmostPath = _rightmostLeafPath;
        if (rightmostPath.Length != 0)
        {
            for (int i = 64; i >= 0; i--)
            {
                if (!_rightmostNodes[i].HasValue) continue;

                (TreePath path, TrieNode node) = _rightmostNodes[i].Value;
                rightmostPath.TruncateMut(i);
                if (rightmostPath != path) continue;

                proofs.Add(node.FullRlp.ToArray());
            }
        }

        return proofs.ToPooledList();
    }

    public void VisitTree(in TreePathContext nodeContext, Hash256 rootHash, TrieVisitContext trieVisitContext)
    {
    }


    public void VisitMissingNode(in TreePathContext ctx, Hash256 nodeHash, TrieVisitContext trieVisitContext)
    {
        throw new TrieException($"Missing node {ctx.Path} {nodeHash}");
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
        _rightmostNodes[ctx.Path.Length] = (ctx.Path, node); // Yes, this is needed. Yes, you can make a special variable like _rightLeafProof.
        if (!ShouldVisit(path))
        {
            if (!_lastNodeFound) _leftLeafProof = (path, node);
            return;
        }

        _leftLeafProof ??= (path, node);

        if (path.Path.CompareTo(_limitHash) >= 0)
        {
            // This leaf is after or at limitHash. This will cause all further ShouldVisit to return false.
            _lastNodeFound = true;
        }

        CollectNode(path, node.Value);

        // We found at least one leaf, don't compare with startHash anymore
        _skipStarthashComparison = true;
    }

    public void VisitCode(in TreePathContext nodeContext, Hash256 codeHash, TrieVisitContext trieVisitContext)
    {
    }

    private byte[]? ConvertFullToSlimAccount(CappedArray<byte> accountRlp)
    {
        return accountRlp.IsNull ? null : _slimDecoder.Encode(_standardDecoder.Decode(new RlpStream(accountRlp))).Bytes;
    }

    private void CollectNode(TreePath path, CappedArray<byte> value)
    {
        _rightmostLeafPath = path;

        byte[]? nodeValue = _isAccountVisitor ? ConvertFullToSlimAccount(value) : value.ToArray();
        _collectedNodes[path.Path] = nodeValue;
        _currentBytesCount += 32 + nodeValue!.Length;
    }

    public void Dispose()
    {
    }
}
