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
using System.Diagnostics;
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

    private long _currentBytesCount;
    private long _currentLeafCount;
    private bool _lastNodeFound = false;
    private readonly ILeafValueCollector _valueCollector;

    // For determining proofs
    private TrieNode?[] _leftmostNodes = new TrieNode?[65];

    // Because we may iterate over the limit, the final right proof may not be of the right value, but it would stop.
    // So we keep two of them for each level, where we try to determine which of the two node is the right one with
    // _rightmostPath.
    private RollingItem<(TreePath, TrieNode)?>?[] _rightmostNodes = new RollingItem<(TreePath, TrieNode)?>?[65];
    private TreePath? _rightmostPath = null;

    private readonly int _nodeLimit;
    private readonly long _byteLimit;

    public bool StoppedEarly { get; set; } = false;
    public bool IsFullDbScan => false;
    public bool IsRangeScan => true;
    private readonly CancellationToken _cancellationToken;

    public ReadFlags ExtraReadFlag { get; }

    public RangeQueryVisitor(
        in ValueHash256 startHash,
        in ValueHash256 limitHash,
        ILeafValueCollector valueCollector,
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
        _valueCollector = valueCollector;
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

        if (_currentLeafCount >= _nodeLimit)
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


    public long GetBytesSize()
    {
        return _currentBytesCount;
    }

    public ArrayPoolList<byte[]> GetProofs()
    {
        HashSet<byte[]> proofs = new();

        if (_leftmostNodes[0] is not null)
        {
            int i = 0;
            while (true)
            {
                if (_leftmostNodes[i] is null) break;

                TrieNode node = _leftmostNodes[i];
                proofs.Add(node.FullRlp.ToArray());

                if (node.IsBranch)
                    i++;
                else if (node.IsExtension)
                    i += node.Key.Length;
                else
                    break;
            }
        }

        if (_rightmostPath.HasValue)
        {
            TreePath rightmostPath = _rightmostPath.Value;
            int i = 0;
            while (true)
            {
                if (_rightmostNodes[i] is null) break;

                TrieNode node = null;
                foreach ((TreePath, TrieNode)? entry in _rightmostNodes[i].Data)
                {
                    if (!entry.HasValue) continue;

                    (TreePath itemPath, TrieNode n) = entry.Value;
                    if (rightmostPath.Truncate(itemPath.Length) == itemPath)
                    {
                        node = n;
                    }
                }

                if (node is null) break;

                proofs.Add(node.FullRlp.ToArray());
                if (node.IsBranch)
                    i++;
                else if (node.IsExtension)
                    i += node.Key.Length;
                else
                    break;
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
        _leftmostNodes[ctx.Path.Length] ??= node;
        (_rightmostNodes[ctx.Path.Length] ??= new RollingItem<(TreePath, TrieNode)?>()).Add((ctx.Path, node));
    }

    public void VisitExtension(in TreePathContext ctx, TrieNode node, TrieVisitContext trieVisitContext)
    {
        _leftmostNodes[ctx.Path.Length] ??= node;
        (_rightmostNodes[ctx.Path.Length] ??= new RollingItem<(TreePath, TrieNode)?>()).Add((ctx.Path, node));
    }

    public void VisitLeaf(in TreePathContext ctx, TrieNode node, TrieVisitContext trieVisitContext, ReadOnlySpan<byte> value)
    {
        _leftmostNodes[ctx.Path.Length] ??= node;
        (_rightmostNodes[ctx.Path.Length] ??= new RollingItem<(TreePath, TrieNode)?>()).Add((ctx.Path, node));

        TreePath path = ctx.Path.Append(node.Key);
        if (!ShouldVisit(path))
        {
            return;
        }

        if (path.Path.CompareTo(_limitHash) >= 0)
        {
            // This leaf is after or at limitHash. This will cause all further ShouldVisit to return false.
            // Yes, we do need to include this as part of the response.
            _lastNodeFound = true;
        }

        CollectNode(path, node.Value);

        // We found at least one leaf, don't compare with startHash anymore
        _skipStarthashComparison = true;
        _rightmostPath = path;
    }

    public void VisitCode(in TreePathContext nodeContext, Hash256 codeHash, TrieVisitContext trieVisitContext)
    {
    }

    private void CollectNode(in TreePath path, CappedArray<byte> value)
    {
        int encodedSize = _valueCollector.Collect(path.Path, value);
        _currentBytesCount += encodedSize;
        _currentLeafCount++;
    }

    public void Dispose()
    {
    }

    public interface ILeafValueCollector
    {
        int Collect(in ValueHash256 path, CappedArray<byte> value);
    }

    // Store two item, and round robin between them on Add
    private class RollingItem<T>
    {
        public T[] Data { get; } = new T[2];
        private int _idx = 0;

        public void Add(T item)
        {
            Data[_idx] = item;
            _idx = 1 - _idx;
        }
    }
}
