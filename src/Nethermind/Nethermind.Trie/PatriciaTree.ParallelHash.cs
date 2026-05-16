// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Cpu;
using Nethermind.Core.Threading;

namespace Nethermind.Trie;

// Devirtualised comparer: passing this readonly struct to Sort<TComparer> lets the JIT specialise
// the sort body and inline Compare, vs the indirect dispatch a Comparison<T> delegate would force.
// Same pattern repeats in BatchedTrieVisitor and PersistentStorageProvider; do not rewrite as lambdas.
internal readonly struct TrieRootJobWeightDescendingComparer : IComparer<TrieRootJob>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Compare(TrieRootJob left, TrieRootJob right) => right.Weight.CompareTo(left.Weight);
}

internal readonly struct TrieRootHashWorkItem(PatriciaTree tree, int weight, int[]? firstNibbleWeights = null)
{
    public readonly PatriciaTree Tree = tree;
    public readonly int Weight = weight;
    public readonly int[]? FirstNibbleWeights = firstNibbleWeights;
}

internal readonly struct TrieRootPartition(
    PatriciaTree tree,
    int jobStart,
    int jobCount,
    int weight,
    bool isEmptyRoot,
    bool needsTopFinalization)
{
    public readonly PatriciaTree Tree = tree;
    public readonly int JobStart = jobStart;
    public readonly int JobCount = jobCount;
    public readonly int Weight = weight;
    public readonly bool IsEmptyRoot = isEmptyRoot;
    public readonly bool NeedsTopFinalization = needsTopFinalization;
}

internal readonly struct TrieRootJob(
    int partitionIndex,
    TrieNode node,
    TreePath path,
    int weight,
    bool isWholeTree)
{
    public readonly int PartitionIndex = partitionIndex;
    public readonly TrieNode Node = node;
    public readonly TreePath Path = path;
    public readonly int Weight = weight;
    public readonly bool IsWholeTree = isWholeTree;
}

public partial class PatriciaTree
{
    private const int MaxRootHashJobsPerTree = 16;
    private const int DominantRootHashChildNumerator = 3;
    private const int DominantRootHashChildDenominator = 4;

    internal void UpdateRootHashParallel(int estimatedWeight = 0)
    {
        using ArrayPoolList<TrieRootHashWorkItem> workItems = new(1);
        workItems.Add(new TrieRootHashWorkItem(this, estimatedWeight));
        UpdateRootHashes(workItems.AsSpan());
    }

    internal static void UpdateRootHashes(ReadOnlySpan<TrieRootHashWorkItem> workItems)
    {
        if (workItems.Length == 0)
        {
            return;
        }

        ArrayPoolList<TrieRootPartition> partitions = new(workItems.Length);
        ArrayPoolList<TrieRootJob> jobs = new(Math.Min(workItems.Length * 4, workItems.Length * MaxRootHashJobsPerTree));
        try
        {
            for (int i = 0; i < workItems.Length; i++)
            {
                TrieRootHashWorkItem workItem = workItems[i];
                ReadOnlySpan<int> firstNibbleWeights = workItem.FirstNibbleWeights is { } weights ? weights : default;
                partitions.Add(workItem.Tree.PartitionForParallelHash(i, workItem.Weight, firstNibbleWeights, MaxRootHashJobsPerTree, ref jobs));
            }

            if (jobs.Count == 1)
            {
                TrieRootJob job = jobs[0];
                partitions[job.PartitionIndex].Tree.ResolveParallelHashJob(in job);
            }
            else if (jobs.Count > 1)
            {
                jobs.Sort<TrieRootJobWeightDescendingComparer>(default);

                ParallelUnbalancedWork.For(
                    0,
                    jobs.Count,
                    RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16,
                    i =>
                    {
                        TrieRootJob job = jobs[i];
                        partitions[job.PartitionIndex].Tree.ResolveParallelHashJob(in job);
                    });
            }

            for (int i = 0; i < partitions.Count; i++)
            {
                partitions[i].Tree.FinalizeParallelHash();
            }
        }
        finally
        {
            jobs.Dispose();
            partitions.Dispose();
        }
    }

    internal TrieRootPartition PartitionForParallelHash(
        int partitionIndex,
        int estimatedWeight,
        ReadOnlySpan<int> firstNibbleWeights,
        int maxJobs,
        ref ArrayPoolList<TrieRootJob> jobs)
    {
        int jobStart = jobs.Count;
        TrieNode? root = RootRef;
        if (root is null)
        {
            return new TrieRootPartition(this, jobStart, 0, 0, isEmptyRoot: true, needsTopFinalization: false);
        }

        if (!root.IsDirty && root.HasKeccak)
        {
            return new TrieRootPartition(this, jobStart, 0, 0, isEmptyRoot: false, needsTopFinalization: false);
        }

        int safeWeight = Math.Max(1, estimatedWeight);
        TreePath path = TreePath.Empty;
        TrieNode splitRoot = root;
        if (!TryWalkDirtyExtensionsToBranch(ref splitRoot, ref path))
        {
            AddRootHashJob(partitionIndex, root, TreePath.Empty, safeWeight, isWholeTree: true, ref jobs);
            return new TrieRootPartition(this, jobStart, 1, safeWeight, isEmptyRoot: false, needsTopFinalization: false);
        }

        int appended = AppendBranchRootHashJobs(partitionIndex, splitRoot, path, estimatedWeight, firstNibbleWeights, maxJobs, ref jobs);
        return new TrieRootPartition(this, jobStart, appended, safeWeight, isEmptyRoot: false, needsTopFinalization: true);
    }

    internal void ResolveParallelHashJob(in TrieRootJob job, ICappedArrayPool? bufferPool = null)
    {
        TreePath path = job.Path;
        job.Node.ResolveKey(TrieStore, ref path, bufferPool, canBeParallel: false);
    }

    internal void FinalizeParallelHash(ICappedArrayPool? bufferPool = null)
    {
        TreePath path = TreePath.Empty;
        RootRef?.ResolveKey(TrieStore, ref path, bufferPool, canBeParallel: false);
        SetRootHash(RootRef?.Keccak ?? EmptyTreeHash, resetObjects: false);
    }

    private bool TryWalkDirtyExtensionsToBranch(ref TrieNode node, ref TreePath path)
    {
        while (node.IsExtension)
        {
            node.AppendChildPath(ref path, 0);
            TrieNode? child = node.GetChildWithChildPath(TrieStore, ref path, 0, keepChildRef: true);
            if (child is null)
            {
                return false;
            }

            if (!child.IsDirty)
            {
                return false;
            }

            node = child;
        }

        return node.IsBranch;
    }

    private int AppendBranchRootHashJobs(
        int partitionIndex,
        TrieNode branch,
        TreePath branchPath,
        int estimatedWeight,
        ReadOnlySpan<int> firstNibbleWeights,
        int maxJobs,
        ref ArrayPoolList<TrieRootJob> jobs)
    {
        int dirtyChildren = 0;
        int totalWeight = 0;
        int maxWeight = 0;
        int maxNibble = -1;
        TrieNode? maxChild = null;
        int fallbackWeight = Math.Max(1, estimatedWeight / TrieNode.BranchesCount);

        for (int i = 0; i < TrieNode.BranchesCount; i++)
        {
            if (!branch.TryGetDirtyChild(i, out TrieNode? child))
            {
                continue;
            }

            int weight = ChildWeight(i, firstNibbleWeights, fallbackWeight);
            dirtyChildren++;
            totalWeight += weight;
            if (weight > maxWeight)
            {
                maxWeight = weight;
                maxNibble = i;
                maxChild = child;
            }
        }

        if (dirtyChildren == 0)
        {
            return 0;
        }

        bool splitDominantChild = maxChild is not null
            && maxJobs > dirtyChildren
            && (dirtyChildren == 1 || maxWeight * DominantRootHashChildDenominator >= totalWeight * DominantRootHashChildNumerator);

        int jobStart = jobs.Count;
        for (int i = 0; i < TrieNode.BranchesCount; i++)
        {
            if (!branch.TryGetDirtyChild(i, out TrieNode? child))
            {
                continue;
            }

            TreePath childPath = branchPath.Append(i);
            int weight = ChildWeight(i, firstNibbleWeights, fallbackWeight);
            if (splitDominantChild && i == maxNibble)
            {
                TrieNode nestedRoot = child;
                TreePath nestedPath = childPath;
                if (TryWalkDirtyExtensionsToBranch(ref nestedRoot, ref nestedPath)
                    && AppendBranchRootHashJobs(
                        partitionIndex,
                        nestedRoot,
                        nestedPath,
                        weight,
                        default,
                        maxJobs - (dirtyChildren - 1),
                        ref jobs) != 0)
                {
                    continue;
                }
            }

            AddRootHashJob(partitionIndex, child, childPath, weight, isWholeTree: false, ref jobs);
        }

        return jobs.Count - jobStart;
    }

    private static int ChildWeight(int nibble, ReadOnlySpan<int> weights, int fallbackWeight) =>
        weights.Length == TrieNode.BranchesCount && weights[nibble] > 0
            ? weights[nibble]
            : fallbackWeight;

    private static void AddRootHashJob(
        int partitionIndex,
        TrieNode node,
        TreePath path,
        int weight,
        bool isWholeTree,
        ref ArrayPoolList<TrieRootJob> jobs) =>
        jobs.Add(new TrieRootJob(partitionIndex, node, path, Math.Max(1, weight), isWholeTree));
}
