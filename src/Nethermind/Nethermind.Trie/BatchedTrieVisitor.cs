// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public class BatchedTrieVisitor
{
    // Not using shared pool so GC can reclaim them later.
    private ArrayPool<(ValueKeccak, SmallTrieVisitContext)> _valueKeccakPool = ArrayPool<(ValueKeccak, SmallTrieVisitContext)>.Create();
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

        // The keccak + context itself should be 40 byte. But the node of the concurrent stack seems to total to this
        // according to dotmemory. Might wanna try something other than ConcurrentStack. Tried Stack with lock,
        // modified ArrayPool and ConcurrentQueue. Curiously, ConcurrentStack seems to work the best probably because
        // other technique create LOHs.
        long recordSize = 112;

        // Generally, at first, we want to attempt to maximize number of partition. This tend to increase throughput
        // compared to increasing job size.
        long partitionCount = visitingOptions.FullScanMemoryBudget / (recordSize * _maxJobSize);

        // 3000 is about the num of file for state on mainnet, so we assume 4000 for an unpruned db. Multiplied by
        // a reasonable num of thread we want to confine to a file. If its too high, the overhead of looping through the
        // stack can get a bit high at the end of the visit. But then again its probably not much.
        long maxPartitionCount = 4000 * Math.Min(4, visitingOptions.MaxDegreeOfParallelism);

        if (partitionCount > maxPartitionCount)
        {
            partitionCount = maxPartitionCount;
            _maxJobSize = (int) (visitingOptions.FullScanMemoryBudget / (partitionCount * recordSize));
        }

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
        ConcurrentStack<(ValueKeccak, SmallTrieVisitContext)>[] nodeToProcess = new ConcurrentStack<(ValueKeccak, SmallTrieVisitContext)>[partitionCount];
        for (int i = 0; i < partitionCount; i++)
        {
            nodeToProcess[i] = new ConcurrentStack<(ValueKeccak, SmallTrieVisitContext)>();
        }

        // Start with the root
        SmallTrieVisitContext rootContext = new(trieVisitContext);
        nodeToProcess[CalculateShardIdx(root)].Push((root, rootContext));
        activeItems = 1;
        queuedItems = 1;
        bool failed = false;

        ArrayPoolList<(TrieNode, SmallTrieVisitContext)>? NextBatch()
        {
            long shardIdx;
            ConcurrentStack<(ValueKeccak, SmallTrieVisitContext)>? theShard;
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
            } while (theShard.IsEmpty);

            ArrayPoolList<(TrieNode, SmallTrieVisitContext)> finalBatch = new(_trieNodePool, _maxJobSize);

            if (activeItems < targetCurrentItems)
            {
                for (int i = 0; i < _maxJobSize; i++)
                {
                    if (!theShard.TryPop(out (ValueKeccak, SmallTrieVisitContext) item)) break;
                    finalBatch.Add((new TrieNode(NodeType.Unknown, item.Item1.ToKeccak()), item.Item2));
                    Interlocked.Decrement(ref queuedItems);
                }
            }
            else
            {
                // So we get more than the batch size, then we sort it by level, and take only the maxNodeBatch nodes with
                // the higher level. This is so that higher level is processed first to reduce memory usage. Its inaccurate,
                // and hacky, but it works.
                (ValueKeccak, SmallTrieVisitContext)[] preSort = _valueKeccakPool.Rent(_maxJobSize * 4);
                int preSortLength = theShard.TryPopRange(preSort);
                Interlocked.Add(ref queuedItems, -preSortLength);

                // Sort by level
                if (activeItems > targetCurrentItems)
                {
                    preSort.AsSpan(0, preSortLength).Sort((item1, item2) => item1.Item2.Level.CompareTo(item2.Item2.Level) * -1);
                }

                int endIdx = Math.Min(_maxJobSize, preSortLength);

                for (int i = 0; i < endIdx; i++)
                {
                    (ValueKeccak keccak, SmallTrieVisitContext ctx) = preSort[i];

                    TrieNode node = resolver.FindCachedOrUnknown(keccak.ToKeccak());
                    finalBatch.Add((node, ctx));
                }

                // Add back what we won't process. In reverse order.
                for (int i = preSortLength - 1; i >= endIdx; i--)
                {
                    theShard.Push(preSort[i]);
                    Interlocked.Increment(ref queuedItems);
                }
                _valueKeccakPool.Return(preSort);
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
                nodeToProcess[shardIdx].Push((keccak, ctx));
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

    public struct SmallTrieVisitContext
    {
        public SmallTrieVisitContext(TrieVisitContext trieVisitContext)
        {
            Level = (byte)trieVisitContext.Level;
            IsStorage = trieVisitContext.IsStorage;
            BranchChildIndex = (byte?)trieVisitContext.BranchChildIndex;
            ExpectAccounts = trieVisitContext.ExpectAccounts;
        }

        public byte Level { get; internal set; }
        public bool IsStorage { get; internal set; }
        public byte? BranchChildIndex { get; internal set; }
        public bool ExpectAccounts { get; init; }

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
