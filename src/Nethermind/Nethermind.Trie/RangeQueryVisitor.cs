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
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Trie;

public class RangeQueryVisitor : ITreeVisitor<TreePathContext>, IDisposable
{
    private byte[]? _startHash;
    private readonly byte[]? _limitHash;

    private readonly bool _isAccountVisitor;

    private long _currentBytesCount;
    private readonly Dictionary<ValueHash256, byte[]> _collectedNodes = new();

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
            _startHash = ArrayPool<byte>.Shared.Rent(64);
            Nibbles.BytesToNibbleBytes(startHash.Bytes, _startHash);
        }

        if (limitHash != ValueKeccak.MaxValue)
        {
            _limitHash = ArrayPool<byte>.Shared.Rent(64);
            Nibbles.BytesToNibbleBytes(limitHash.Bytes, _limitHash);
        }

        _cancellationToken = cancellationToken;
        _isAccountVisitor = isAccountVisitor;
        _nodeLimit = nodeLimit;
        _byteLimit = byteLimit;
        _hardByteLimit = hardByteLimit;
    }

    // to check if the node should be visited on the based of its path and limitHash
    private bool ShouldVisit(Span<byte> path)
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
        // The comparator is a bit special. If startHash > path, it would still return 1.
        if (_startHash != null)
        {
            compResult = Bytes.Comparer.CompareGreaterThan(path, _startHash);
            if (compResult < 0)
            {
                return false;
            }
        }

        if (_limitHash != null)
        {
            compResult = path.SequenceCompareTo(_limitHash);
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
        return ShouldVisit(ctx.Path.ToNibbles());
    }


    public (Dictionary<ValueHash256, byte[]>, long) GetNodesAndSize()
    {
        return (_collectedNodes, _currentBytesCount);
    }

    public void VisitTree(in TreePathContext nodeContext, Hash256 rootHash, TrieVisitContext trieVisitContext)
    {
    }


    public void VisitMissingNode(in TreePathContext ctx, Hash256 nodeHash, TrieVisitContext trieVisitContext)
    {
    }

    public void VisitBranch(in TreePathContext ctx, TrieNode node, TrieVisitContext trieVisitContext)
    {
    }

    public void VisitExtension(in TreePathContext ctx, TrieNode node, TrieVisitContext trieVisitContext)
    {
    }

    public void VisitLeaf(in TreePathContext ctx, TrieNode node, TrieVisitContext trieVisitContext, ReadOnlySpan<byte> value)
    {
        TreePath path = ctx.Path.Append(node.Key);
        if (!ShouldVisit(path.ToNibbles())) return;
        CollectNode(path, node.Value);
        // We found at least one leaf, don't compare with startHash anymore
        if (_startHash != null)
        {
            ArrayPool<byte>.Shared.Return(_startHash);
            _startHash = null;
        }
    }

    public void VisitCode(in TreePathContext nodeContext, Hash256 codeHash, TrieVisitContext trieVisitContext)
    {
        throw new NotImplementedException();
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
        byte[]? nodeValue = _isAccountVisitor ? ConvertFullToSlimAccount(value) : value.ToArray();
        _collectedNodes[path.Path] = nodeValue;
        _currentBytesCount += 32 + nodeValue!.Length;
    }

    public void Dispose()
    {
        if (_startHash != null) ArrayPool<byte>.Shared.Return(_startHash);
        if (_limitHash != null) ArrayPool<byte>.Shared.Return(_limitHash);
    }
}
