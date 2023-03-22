// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public class BatchedTrieVisitor
{
    // Not using shared pool so GC can reclaim them later.
    private ArrayPool<Job> _jobArrayPool = ArrayPool<Job>.Create();
    private ArrayPool<(TrieNode, SmallTrieVisitContext)> _trieNodePool = ArrayPool<(TrieNode, SmallTrieVisitContext)>.Create();
    private int _maxJobSize;

    public void BatchedAccept(
        ITreeVisitor visitor,
        ITrieNodeResolver resolver,
        ValueKeccak root,
        TrieVisitContext trieVisitContext,
        VisitingOptions visitingOptions)
    {
        _maxJobSize = 128;

        // The keccak + context itself should be 40 byte. But the measured byte seems to be 52 from GC stats POV.
        // The * 2 is just margin. RSS is still higher though, but that could be due to more deserialization.
        long recordSize = 52 * 2;
        long recordCount = visitingOptions.FullScanMemoryBudget / recordSize;

        // Generally, at first, we want to attempt to maximize number of partition. This tend to increase throughput
        // compared to increasing job size.
        long partitionCount = recordCount / _maxJobSize;

        // 3000 is about the num of file for state on mainnet, so we assume 4000 for an unpruned db. Multiplied by
        // a reasonable num of thread we want to confine to a file. If its too high, the overhead of looping through the
        // stack can get a bit high at the end of the visit. But then again its probably not much.
        long maxPartitionCount = 4000 * Math.Min(4, visitingOptions.MaxDegreeOfParallelism);

        if (partitionCount > maxPartitionCount)
        {
            partitionCount = maxPartitionCount;
            _maxJobSize = (int) (recordCount / partitionCount);
        }

        // Calculating estimated pool margin at about 5% of total working size. The queue size fluctuate a bit so
        // this is to reduce allocation.
        long estimatedPoolMargin = (long)((recordCount / 128) * 0.05);
        ObjectPool<CompactStack<Job>.Node> jobPool = new DefaultObjectPool<CompactStack<Job>.Node>(
            new CompactStack<Job>.ObjectPoolPolicy(128), (int) estimatedPoolMargin);

        long currentPointer = 0;
        long queuedItems = 0;
        long activeItems = 0;
        long targetCurrentItems = partitionCount * _maxJobSize;

        // Determine the locality of the key. I guess if you use paprika or something, you'd need to modify this.
        int CalculateShardIdx(ValueKeccak key)
        {
            uint number = BinaryPrimitives.ReadUInt32BigEndian(key.Span);
            return (int)(number * (ulong) partitionCount / uint.MaxValue);
        }

        // This need to be very small
        CompactStack<Job>[] nodeToProcess = new CompactStack<Job>[partitionCount];
        for (int i = 0; i < partitionCount; i++)
        {
            nodeToProcess[i] = new CompactStack<Job>(jobPool);
        }

        // Start with the root
        SmallTrieVisitContext rootContext = new(trieVisitContext);
        nodeToProcess[CalculateShardIdx(root)].Push(new Job(root, rootContext));
        activeItems = 1;
        queuedItems = 1;
        bool failed = false;

        ArrayPoolList<(TrieNode, SmallTrieVisitContext)>? NextBatch()
        {
            long shardIdx;
            CompactStack<Job>? theShard;
            do
            {
                shardIdx = Interlocked.Increment(ref currentPointer);
                if (shardIdx == partitionCount)
                {
                    Interlocked.Add(ref currentPointer, -partitionCount);

                    GC.Collect(); // Simulate GC collect of standard visitor
                }
                shardIdx = shardIdx % partitionCount;

                if (activeItems == 0 || failed)
                {
                    // Its finished
                    return null;
                }

                if (queuedItems == 0)
                {
                    // Just a small timeout to prevent threads from loading CPU
                    // Note, there are other threads also going through the stacks, so its fine to have this high.
                    Thread.Sleep(TimeSpan.FromMilliseconds(100));
                }

                theShard = nodeToProcess[shardIdx];
                lock (theShard)
                {
                    if (!theShard.IsEmpty) break;
                }
            } while (true);

            ArrayPoolList<(TrieNode, SmallTrieVisitContext)> finalBatch = new(_trieNodePool, _maxJobSize);

            if (activeItems < targetCurrentItems)
            {
                lock (theShard)
                {
                    for (int i = 0; i < _maxJobSize; i++)
                    {
                        if (!theShard.TryPop(out Job item)) break;
                        finalBatch.Add((new TrieNode(NodeType.Unknown, item.Key.ToKeccak()), item.Context));
                        Interlocked.Decrement(ref queuedItems);
                    }
                }
            }
            else
            {
                // So we get more than the batch size, then we sort it by level, and take only the maxNodeBatch nodes with
                // the higher level. This is so that higher level is processed first to reduce memory usage. Its inaccurate,
                // and hacky, but it works.
                ArrayPoolList<Job> preSort = new(_jobArrayPool, _maxJobSize * 4);
                lock (theShard)
                {
                    for (int i = 0; i < _maxJobSize*4; i++)
                    {
                        if (!theShard.TryPop(out Job item)) break;
                        preSort.Add(item);
                        Interlocked.Decrement(ref queuedItems);
                    }
                }

                // Sort by level
                if (activeItems > targetCurrentItems)
                {
                    preSort.AsSpan().Sort((item1, item2) => item1.Context.Level.CompareTo(item2.Context.Level) * -1);
                }

                int endIdx = Math.Min(_maxJobSize, preSort.Count);

                for (int i = 0; i < endIdx; i++)
                {
                    Job job = preSort[i];

                    TrieNode node = resolver.FindCachedOrUnknown(job.Key.ToKeccak());
                    finalBatch.Add((node, job.Context));
                }

                // Add back what we won't process. In reverse order.
                lock (theShard)
                {
                    for (int i = preSort.Count - 1; i >= endIdx; i--)
                    {
                        theShard.Push(preSort[i]);
                        Interlocked.Increment(ref queuedItems);
                    }
                }
                preSort.Dispose();
            }

            return finalBatch;
        }

        void OnBatchResult(ArrayPoolList<(TrieNode, SmallTrieVisitContext)> batchResult)
        {
            // Reverse order is important so that higher level appear at the end of the stack.
            for (int i = batchResult.Count - 1; i >= 0; i--)
            {
                (TrieNode trieNode, SmallTrieVisitContext ctx) = batchResult[i];
                if (trieNode.NodeType == NodeType.Unknown && trieNode.FullRlp != null)
                {
                    // Inline node. Seems rare, so its fine to create new list for this. Does not have a keccak
                    // to queue, so we'll just process it inline.
                    ArrayPoolList<(TrieNode, SmallTrieVisitContext)> recursiveResult = new(1);
                    trieNode.ResolveNode(resolver);
                    Interlocked.Increment(ref activeItems);
                    trieNode.AcceptResolvedNode(visitor, resolver, ctx, recursiveResult);
                    OnBatchResult(recursiveResult);
                    recursiveResult.Dispose();
                    continue;
                }

                ValueKeccak keccak = trieNode.Keccak;
                int shardIdx = CalculateShardIdx(keccak);
                Interlocked.Increment(ref activeItems);
                Interlocked.Increment(ref queuedItems);

                var theShard = nodeToProcess[shardIdx];
                lock (theShard)
                {
                    theShard.Push(new Job(keccak, ctx));
                }
            }

            Interlocked.Decrement(ref activeItems);
        }

        try
        {
            Task[]? tasks = Enumerable.Range(0, trieVisitContext.MaxDegreeOfParallelism)
                .Select((_) => Task.Run(() => BatchedThread(visitor, resolver, NextBatch, OnBatchResult)))
                .ToArray();

            Task.WhenAll(tasks).Wait();
        }
        catch (Exception)
        {
            failed = true;
            throw;
        }
    }

    private void BatchedThread(ITreeVisitor visitor,
        ITrieNodeResolver resolver,
        Func<ArrayPoolList<(TrieNode, SmallTrieVisitContext)>?> getNextBatch,
        Action<ArrayPoolList<(TrieNode, SmallTrieVisitContext)>> returnBatchResult)
    {
        ArrayPoolList<(TrieNode, SmallTrieVisitContext)>? currentBatch;
        ArrayPoolList<(TrieNode, SmallTrieVisitContext)> nextToProcesses = new(_maxJobSize);
        ArrayPoolList<int> resolveOrdering = new(_maxJobSize);
        while ((currentBatch = getNextBatch()) != null)
        {
            // Storing the idx separately as the ordering is important to reduce memory (approximate dfs ordering)
            // but the path ordering is important for read amplification
            resolveOrdering.Clear();
            for (int i = 0; i < currentBatch.Count; i++)
            {
                TrieNode cur = currentBatch[i].Item1;

                cur.ResolveKey(resolver, false);

                SmallTrieVisitContext ctx = currentBatch[i].Item2;

                if (cur.FullRlp != null) continue;
                if (cur.Keccak is null) throw new TrieException($"Unable to resolve node without Keccak. ctx: {ctx.Level}, {ctx.ExpectAccounts}, {ctx.IsStorage}, {ctx.BranchChildIndex}");

                resolveOrdering.Add(i);
            }

            // This innocent looking sort is surprisingly effective when batch size is large enough. The sort itself
            // take about 0.1% of the time, so not very cpu intensive in this case.
            resolveOrdering
                .AsSpan()
                .Sort((item1, item2) => currentBatch[item1].Item1.Keccak.CompareTo(currentBatch[item2].Item1.Keccak));

            // This loop is about 60 to 70% of the time spent. If you set very high memory budget, this drop to about 50MB.
            for (int i = 0; i < resolveOrdering.Count; i++)
            {
                int idx = resolveOrdering[i];

                (TrieNode nodeToResolve, SmallTrieVisitContext ctx) = currentBatch[idx];
                try
                {
                    Keccak theKeccak = nodeToResolve.Keccak;
                    nodeToResolve.ResolveNode(resolver);
                    nodeToResolve.Keccak = theKeccak; // The resolve may set a key which clear the keccak
                }
                catch (TrieException)
                {
                    visitor.VisitMissingNode(nodeToResolve.Keccak, ctx.ToVisitContext());
                }
            };

            // Going in reverse to reduce memory
            for (int i = currentBatch.Count - 1; i >= 0; i--)
            {
                (TrieNode nodeToResolve, SmallTrieVisitContext ctx) = currentBatch[i];

                nextToProcesses.Clear();
                if (nodeToResolve.FullRlp == null)
                {
                    // Still need to decrement counter
                    returnBatchResult(nextToProcesses);
                    return; // missing node
                }

                nodeToResolve.AcceptResolvedNode(visitor, resolver, ctx, nextToProcesses);
                returnBatchResult(nextToProcesses);
            }

            currentBatch.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct Job
    {
        public ValueKeccak Key;
        public SmallTrieVisitContext Context;

        public Job(ValueKeccak key, SmallTrieVisitContext context)
        {
            Key = key;
            Context = context;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SmallTrieVisitContext
    {
        public SmallTrieVisitContext(TrieVisitContext trieVisitContext)
        {
            Level = (byte)trieVisitContext.Level;
            IsStorage = trieVisitContext.IsStorage;
            if (trieVisitContext.BranchChildIndex != null)
            {
                _branchChildIndex = (byte)trieVisitContext.BranchChildIndex!;
            }
            ExpectAccounts = trieVisitContext.ExpectAccounts;
        }

        public byte Level { get; internal set; }
        public bool IsStorage { get; internal set; }

        private byte _branchChildIndex = 255;
        public bool ExpectAccounts { get; init; }

        public byte? BranchChildIndex
        {
            get => _branchChildIndex == 255 ? null : _branchChildIndex;
            internal set
            {
                if (value == null)
                {
                    _branchChildIndex = 255;
                }
                else
                {
                    _branchChildIndex = (byte)value;
                }
            }
        }

        public SmallTrieVisitContext Clone() => (SmallTrieVisitContext)MemberwiseClone();

        public TrieVisitContext ToVisitContext()
        {
            return new TrieVisitContext()
            {
                Level = Level,
                IsStorage = IsStorage,
                BranchChildIndex = BranchChildIndex,
                ExpectAccounts = ExpectAccounts
            };
        }
    }
}
