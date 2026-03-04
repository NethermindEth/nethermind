// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Org.BouncyCastle.Crypto.Digests;
using Prometheus;

namespace Nethermind.State.Flat;

public class BatchedTrieWarmer
{
    private const int MaxKeyStackAlloc = 64;

    // TODO: need a stack
    private MpmcBoundedStack<TraverseCtx> _traverseJobs = new MpmcBoundedStack<TraverseCtx>(1024 * 128);
    private MpmcBoundedStack<TraverseCtx> _ioJob = new MpmcBoundedStack<TraverseCtx>(1024 * 128);

    private static Counter.Child _skipMissing = TrieWarmer._trieWarmEr.WithLabels("skip_missing");
    private static Counter.Child _completed = TrieWarmer._trieWarmEr.WithLabels("completed");
    private static Counter.Child _skipKeccakMismatch = TrieWarmer._trieWarmEr.WithLabels("keccak_mismatch");
    private static Counter.Child _ioPush = TrieWarmer._trieWarmEr.WithLabels("io_push");
    private static Counter.Child _traverseCount = TrieWarmer._trieWarmEr.WithLabels("traverse");

    public interface IBulkGetter
    {
        bool IsCompleted { get; }
        bool EnableBulkGet { get; }
        byte[]?[] BulkGet(Span<(Hash256?, TreePath)> keys);

        TraverseCtx CreateCtxFor(FlatStorageTree storageTree, UInt256 slot);
        TraverseCtx CreateCtxFor(Address address);
    }

    public struct TraverseCtx
    {
        public Hash256? Address;
        public IBulkGetter BulkGetter;

        public byte[] PathNibbles;
        public ITrieNodeResolver TrieStore;
        public TrieNode? Node;
        public TreePath Path;
    }

    public long EstimatedJobCount => _traverseJobs.EstimatedJobCount + _ioJob.EstimatedJobCount;

    public bool PushJob(TraverseCtx ctx)
    {
        return _traverseJobs.TryPush(ctx);
    }

    private int MinJobBatch = 8;
    private int JobBatchThreshold = 128;

    public bool MaybeHandleBulkIo()
    {
        if (_ioJob.EstimatedJobCount <= MinJobBatch) return false;

        return DoHandleBulkIo();
    }

    public bool MaybeHandleSmallIo()
    {
        return DoHandleBulkIo();
    }

    private bool DoHandleBulkIo()
    {
        using ArrayPoolList<TraverseCtx> bulkRequest = new ArrayPoolList<TraverseCtx>(JobBatchThreshold);
        IBulkGetter? bulkGetter = null;

        while (_ioJob.TryPop(out TraverseCtx ctx))
        {
            if (!ctx.Node!.FullRlp.IsNullOrEmpty)
            {
                // Could just be some kind concurrent job
                _traverseJobs.TryPush(ctx);
                continue;
            }

            if (ctx.BulkGetter.IsCompleted)
            {
                _completed.Inc();
                continue;
            }

            if (bulkGetter == null) bulkGetter = ctx.BulkGetter;
            else if (!ReferenceEquals(bulkGetter, ctx.BulkGetter))
            {
                Console.Error.WriteLine("Different bulkgetter");
                // Different bulk getter? One of this should be closed. Lets just drop it, and do what we can for now.
                break;
            }

            bulkRequest.Add(ctx);
            if (bulkRequest.Count >= JobBatchThreshold) break;
        }

        if (bulkRequest.Count == 0) return false;

        using ArrayPoolList<(Hash256?, TreePath)> keys = new ArrayPoolList<(Hash256?, TreePath)>(JobBatchThreshold);
        foreach (TraverseCtx traverseCtx in bulkRequest)
        {
            keys.Add((traverseCtx.Address, traverseCtx.Path));
        }

        byte[]?[] result = bulkGetter!.BulkGet(keys.AsSpan());

        for (int i = 0; i < bulkRequest.Count; i++)
        {
            if (result[i] is null)
            {
                _skipMissing.Inc();
                // Trie exception? Lets just drop it.
                Console.Error.WriteLine("Got missing");
                continue;
            }

            /*
            if (Keccak.Compute(result[i]) != bulkRequest[i].Node!.Keccak)
            {
                Console.Error.WriteLine($"Mismatch {Keccak.Compute(result[i])} vs {bulkRequest[i].Node!.Keccak}, {bulkRequest[i].Path}, {result[i]?.ToHexString()}");
                _skipKeccakMismatch.Inc();
                // Need to investigate why this happens
                continue;
            }
            */

            bulkRequest[i].Node!.SetRlp(result[i]!);
            _traverseJobs.TryPush(bulkRequest[i]);
        }

        return true;
    }

    public bool MaybeHandleTraverseJob()
    {
        if (!_traverseJobs.TryPop(out var ctx)) return false;
        if (ctx.Node == null)
        {
            // TODO: look into this
            // Console.Error.WriteLine("Missing node");
            return true;
        }

        if (!DoWarmUpPath(ref ctx, ctx.PathNibbles.AsSpan()[ctx.Path.Length..], ref ctx.Path, ref ctx.Node, false))
        {
            _ioPush.Inc();
            // Incomplete
            _ioJob.TryPush(ctx);
            return true;
        }

        _traverseCount.Inc();
        return true;
    }

    private bool DoWarmUpPath(ref TraverseCtx ctx, Span<byte> remainingKey, ref TreePath path, ref TrieNode node, bool warmUpPotentialNewNode)
    {
        try
        {
            while (true)
            {
                if (node is null)
                {
                    // If node read, then missing node. If value read.... what is it suppose to be then?
                    return true;
                }

                if (node.IsSealed && node.Keccak is not null)
                    node = ctx.TrieStore.FindCachedOrUnknown(path, node!.Keccak);
                if (node.NodeType == NodeType.Unknown && node.FullRlp.IsNullOrEmpty)
                {
                    return false;
                }

                node.ResolveNode(ctx.TrieStore, path);

                if (node.IsLeaf || node.IsExtension)
                {
                    int commonPrefixLength = remainingKey.CommonPrefixLength(node.Key);
                    if (commonPrefixLength == node.Key!.Length)
                    {
                        if (node.IsLeaf)
                        {
                            // Um..... leaf cannot have child
                            return true;
                        }

                        // Continue traversal to the child of the extension
                        path.AppendMut(node.Key);
                        TrieNode? extensionChild =
                            node.GetChildWithChildPath(ctx.TrieStore, ref path, 0, keepChildRef: true);
                        remainingKey = remainingKey[node!.Key.Length..];
                        node = extensionChild!;

                        continue;
                    }

                    // No node match
                    return true;
                }

                int nextNib = remainingKey[0];

                if (warmUpPotentialNewNode)
                {
                    // When a node (this path) is deleted and its parent is a branch with only one other child, it
                    // will get replaced to an extension, in which case, the other only child need to be loaded.
                    // Node: this will fail to consider if multiple child was deleted though...
                    int nodeIdxToWarmup = -1;
                    for (int i = 0; i < TrieNode.BranchesCount; i++)
                    {
                        if (i == nextNib) continue;

                        if (!node.IsChildNull(i))
                        {
                            if (nodeIdxToWarmup != -1)
                            {
                                // So more than one non null node that is not part of this path.
                                nodeIdxToWarmup = -1;
                                break;
                            }
                            else
                            {
                                nodeIdxToWarmup = i;
                            }
                        }
                    }

                    if (nodeIdxToWarmup != -1)
                    {
                        path.AppendMut(nodeIdxToWarmup);
                        TrieNode? theOtherOnlyChild = node.GetChildWithChildPath(ctx.TrieStore, ref path,
                            nodeIdxToWarmup, keepChildRef: true);
                        theOtherOnlyChild?.ResolveNode(ctx.TrieStore, path);
                        path.TruncateOne();
                    }
                }

                path.AppendMut(nextNib);
                TrieNode? child = node.GetChildWithChildPath(ctx.TrieStore, ref path, nextNib, keepChildRef: true);

                // Continue loop with child as current node
                node = child!;
                remainingKey = remainingKey[1..];
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Warmup traversal failed {e}");
            return true;
        }
    }
}
