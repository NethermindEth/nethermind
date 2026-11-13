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
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.Trie;

public class RangeQueryVisitor : INodeRecordVisitor<TreePathContext>, IDisposable
{
    private bool _skipStarthashComparison = false;
    private TreePath _startHash;
    private readonly ValueHash256 _limitHash;

    private long _currentBytesCount;
    private long _currentLeafCount;
    private bool _lastNodeFound = false;
    private readonly ILeafValueCollector _valueCollector;

    // For determining proofs
    private readonly BoundaryNode[] _leftmostNodes = new BoundaryNode[65];
    private readonly BoundaryNode[] _rightmostNodes = new BoundaryNode[65];

    private readonly int _nodeLimit;
    private readonly long _byteLimit;

    public bool StoppedEarly { get; set; } = false;
    public bool IsFullDbScan => false;
    public bool IsRangeScan => true;
    public bool ExpectAccounts => false;
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

    public bool ShouldVisit(in TreePathContext ctx, in ValueHash256 nextNode) => ShouldVisit(ctx.Path);


    public long GetBytesSize() => _currentBytesCount;

    public ArrayPoolList<byte[]> GetProofs()
    {
        HashSet<byte[]> proofs = new(Bytes.EqualityComparer);

        AddToProof(_leftmostNodes);
        AddToProof(_rightmostNodes);

        return proofs.ToPooledList();

        void AddToProof(BoundaryNode[] boundaryNodes)
        {
            int i = 0;
            while (true)
            {
                BoundaryNode node = boundaryNodes[i];
                if (node.IsEmpty) break;

                proofs.Add(node.ToArray());

                if (node.ChildDepthOffset == 0) break;
                i += node.ChildDepthOffset;
            }
        }
    }

    public void VisitTree(in TreePathContext nodeContext, in ValueHash256 rootHash)
    {
    }


    public void VisitMissingNode(in TreePathContext ctx, in ValueHash256 nodeHash) => throw new TrieException($"Missing node {ctx.Path} {nodeHash}");

    public void VisitBranch(in TreePathContext ctx, NodeRecordRef node) => TrackBoundary(ctx.Path.Length, node, 1);

    public void VisitExtension(in TreePathContext ctx, NodeRecordRef node) => TrackBoundary(ctx.Path.Length, node, node.KeyLength(0));

    public void VisitLeaf(in TreePathContext ctx, NodeRecordRef node)
    {
        TrackBoundary(ctx.Path.Length, node, 0);

        Span<byte> key = stackalloc byte[node.KeyLength(0)];
        node.CopyKey(0, key);
        TreePath path = ctx.Path.Append(key);
        if (!ShouldVisit(path))
        {
            return;
        }

        if (path.Path.CompareTo(_limitHash) >= 0 || _cancellationToken.IsCancellationRequested)
        {
            // This leaf is after or at limitHash. This will cause all further ShouldVisit to return false.
            // Yes, we do need to include this as part of the response.
            // Note: Cancellation must happen at the leaf or the proof may break
            _lastNodeFound = true;
        }

        CollectNode(path, node.LeafValue);

        // We found at least one leaf, don't compare with startHash anymore
        _skipStarthashComparison = true;
    }

    public void VisitAccount(in TreePathContext nodeContext, NodeRecordRef node, in AccountStruct accountStruct)
    {
    }

    private void TrackBoundary(int depth, NodeRecordRef node, int childDepthOffset)
    {
        if (_leftmostNodes[depth].IsEmpty) _leftmostNodes[depth].Set(node.Rlp, childDepthOffset);
        _rightmostNodes[depth].Set(node.Rlp, childDepthOffset);
    }

    private void CollectNode(in TreePath path, ReadOnlySpan<byte> value)
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
        int Collect(in ValueHash256 path, ReadOnlySpan<byte> value);
    }

    /// <summary>A visited node's RLP, copied because its record does not outlive the visit.</summary>
    private struct BoundaryNode
    {
        private byte[]? _rlp;
        private int _length;

        /// <summary>The depth from this node to the next node on its boundary path; 0 for a leaf, which ends the path.</summary>
        public int ChildDepthOffset { get; private set; }

        public readonly bool IsEmpty => _rlp is null;

        public readonly byte[] ToArray() => _rlp.AsSpan(0, _length).ToArray();

        /// <remarks>Reuses the copy's buffer, as the rightmost node at a depth changes on every visit there.</remarks>
        public void Set(ReadOnlySpan<byte> rlp, int childDepthOffset)
        {
            if (_rlp is null || _rlp.Length < rlp.Length) _rlp = new byte[rlp.Length];
            rlp.CopyTo(_rlp);
            _length = rlp.Length;
            ChildDepthOffset = childDepthOffset;
        }
    }
}
