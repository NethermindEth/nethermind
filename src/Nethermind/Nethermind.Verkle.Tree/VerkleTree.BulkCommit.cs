// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks.Dataflow;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Tree.TreeNodes;
using Nethermind.Verkle.Tree.Utils;

namespace Nethermind.Verkle.Tree;

public partial class VerkleTree
{
    private void CommitBulk()
    {
        if (_leafUpdateCache.Count == 0) return;

        if (_logger.IsDebug) _logger.Debug($"VT Commit: SubTree Count:{_leafUpdateCache.Count}");

        using ArrayPoolList<KeyValuePair<byte[], (LeafUpdateDelta?, InternalNode?)>> leafDeltas =
            _leafUpdateCache
                .Select((update) => new KeyValuePair<byte[], (LeafUpdateDelta?, InternalNode?)>(update.Key, (update.Value, null)))
                .ToPooledList(_leafUpdateCache.Count);

        // Sort the leaf delta first
        leafDeltas.AsSpan().Sort((kv1, kv2) => Bytes.BytesComparer.Compare(kv1.Key, kv2.Key));

        _ = CommitBulkParallel(leafDeltas, 0);

        _leafUpdateCache.Clear();
    }

    /// <summary>
    /// Parallel version of `CommitBulk`. Only parallelize current (top) level.
    /// </summary>
    /// <param name="leafDeltas"></param>
    /// <param name="depth"></param>
    /// <returns></returns>
    private FrE CommitBulkParallel(ArrayPoolList<KeyValuePair<byte[], (LeafUpdateDelta?, InternalNode?)>> leafDeltas, int depth)
    {
        Span<byte> key = leafDeltas[0].Key.AsSpan()[..depth];
        InternalNode node = GetInternalNode(key);

        if (node?.IsBranchNode != true || leafDeltas.Count == 1)
        {
            return CommitBulkRecursive(leafDeltas.AsSpan(), depth);
        }

        int sectionStart = 0;
        byte sectionStartByte = leafDeltas[0].Key[depth];

        McsLock deltaLock = new McsLock();
        using ArrayPoolList<(FrE, int)> deltaOps = new ArrayPoolList<(FrE, int)>(256);

        ActionBlock<(int, int, byte)> accumulateDelta = new ActionBlock<(int, int, byte)>(input =>
            {
                (int start, int end, byte sectionByte) = input;
                // Get the delta for the previous section
                FrE sectionDelta = CommitBulkRecursive(leafDeltas.AsSpan()[start..end], depth + 1);

                using McsLock.Disposable _ = deltaLock.Acquire();
                deltaOps.Add((sectionDelta, sectionByte));
            },
            new ExecutionDataflowBlockOptions()
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
            });

        // TODO: Can use binary search
        for (int i = 0; i < leafDeltas.Count; i++)
        {
            if (leafDeltas[i].Key[depth] != sectionStartByte)
            {
                accumulateDelta.Post((sectionStart, i, sectionStartByte));

                // Start a new section
                sectionStartByte = leafDeltas[i].Key[depth];
                sectionStart = i;
            }
        }

        {
            accumulateDelta.Post((sectionStart, leafDeltas.Count, sectionStartByte));
        }

        accumulateDelta.Complete();
        accumulateDelta.Completion.Wait();

        Banderwagon delta = Committer.MultiScalarMul(deltaOps.AsSpan());
        FrE deltaFre = node.UpdateCommitment(delta);
        SetInternalNode(key, node);
        return deltaFre;
    }

    /// <summary>
    /// Commit all `leafDeltas` at once. `leafDeltas` must be sorted by key. It work by partitioning `leafDeltas` by its
    /// key at the specified depth and recursively commit the partitioned section, returning the delta which get multiplied
    /// by the partition key and the applied to the branch which is the current node.
    /// </summary>
    /// <param name="leafDeltas"></param>
    /// <param name="depth"></param>
    /// <returns></returns>
    private FrE CommitBulkRecursive(ReadOnlySpan<KeyValuePair<byte[], (LeafUpdateDelta?, InternalNode?)>> leafDeltas, int depth)
    {
        Span<byte> key = leafDeltas[0].Key.AsSpan()[..depth];
        InternalNode node = GetInternalNode(key);

        if (leafDeltas.Length == 1)
        {
            if (node == null || (node.IsStem && Banderwagon.Equals(node.InternalCommitment.Point, Banderwagon.Identity)))
            {
                if (leafDeltas[0].Value.Item1 != null)
                {
                    // New stem
                    node = new InternalNode(VerkleNodeType.StemNode, leafDeltas[0].Key);
                    FrE deltaFre = node.UpdateCommitment(leafDeltas[0].Value.Item1.Value);
                    deltaFre += node.InitCommitmentHash!.Value;
                    SetInternalNode(key, node);
                    return deltaFre;
                }
                else
                {
                    // Stem that was moved where the path used was converted to a branch
                    node = leafDeltas[0].Value.Item2!;
                    FrE deltaFre = node.InternalCommitment.PointAsField;
                    SetInternalNode(key, node);
                    return deltaFre;
                }
            }

            if (node.IsStem && VerkleUtils.GetPathDifference(node.Stem!.Bytes, leafDeltas[0].Key) == 31)
            {
                Debug.Assert(leafDeltas[0].Value.Item1.HasValue);

                // Stem update of shared key path is 31
                node = node.Clone();
                FrE deltaFre = node.UpdateCommitment(leafDeltas[0].Value.Item1!.Value);
                SetInternalNode(key, node);
                return deltaFre;
            }

            // At this point, node is a branch
            // OR node is a stem which need to be splitted
        }

        int sectionStart = 0;
        byte sectionStartByte = leafDeltas[0].Key[depth];

        // When the current node is a stem, this key will be replaced with a branch, and the current stem
        // need to be moved to a different key. To do that, the current stem is added as part of a new partition
        // backed by a new list.
        bool checkStem = node?.IsStem == true;
        ArrayPoolList<KeyValuePair<byte[], (LeafUpdateDelta?, InternalNode)>>? newSection = null;

        // If the current section byte is the same as the stem, we create a `newSection` which indicate that this section
        // is not backed by the original span.
        if (checkStem && node.Stem!.Bytes[depth] == sectionStartByte) newSection = new ArrayPoolList<KeyValuePair<byte[], (LeafUpdateDelta?, InternalNode)>>(0);

        // TODO: Can use binary search
        Banderwagon delta = Banderwagon.Identity;
        for (int i = 0; i < leafDeltas.Length; i++)
        {
            if (leafDeltas[i].Key[depth] != sectionStartByte)
            {
                AddDelta(i, leafDeltas);

                // Start a new section
                sectionStartByte = leafDeltas[i].Key[depth];
                sectionStart = i;
                if (checkStem && node.Stem!.Bytes[depth] == sectionStartByte) newSection = new ArrayPoolList<KeyValuePair<byte[], (LeafUpdateDelta?, InternalNode)>>(0);
            }

            if (newSection == null) continue;

            if (!checkStem)
            {
                newSection.Add(leafDeltas[i]);
                continue;
            }

            int compare = Bytes.BytesComparer.Compare(node.Stem.Bytes, leafDeltas[i].Key);
            switch (compare)
            {
                case 0:
                {
                    // It match exactly? Then its an update, we update the current stem and add it to the new section
                    InternalNode clonedNode = node.Clone();
                    clonedNode.UpdateCommitment(leafDeltas[i].Value.Item1!.Value);

                    checkStem = false;
                    newSection.Add(
                        new KeyValuePair<byte[], (LeafUpdateDelta?, InternalNode)>(leafDeltas[i].Key,
                            (null, clonedNode)));
                    break;
                }
                case < 0:
                    // The current stem is less than the leaf update delta, so add the current stem first.
                    newSection.Add(
                        new KeyValuePair<byte[], (LeafUpdateDelta?, InternalNode)>(node.Stem.Bytes, (null, node)));
                    checkStem = false;
                    newSection.Add(leafDeltas[i]);
                    break;
                default:
                    newSection.Add(leafDeltas[i]);
                    break;
            }
        }

        // Last section
        AddDelta(leafDeltas.Length, leafDeltas);

        // The current stem is still not set.
        if (checkStem)
        {
            newSection = new ArrayPoolList<KeyValuePair<byte[], (LeafUpdateDelta?, InternalNode)>>(1);
            newSection.Add(new KeyValuePair<byte[], (LeafUpdateDelta?, InternalNode)>(node.Stem.Bytes, (null, node)));
            var recursiveDelta = CommitBulkRecursive(newSection.AsSpan(), depth + 1);
            var mulDelta = Committer.ScalarMul(recursiveDelta, node.Stem.Bytes[depth]);

            delta += mulDelta;
            newSection.Dispose();
        }

        // Does this create a closure or its implemented inline?
        void AddDelta(int newSectionStart, ReadOnlySpan<KeyValuePair<byte[], (LeafUpdateDelta?, InternalNode?)>> leafDeltas)
        {
            if (checkStem && newSection != null)
            {
                // So the new section still does not add the current stem meaning that all stem in section is less than
                // the current stem.
                newSection.Add(new KeyValuePair<byte[], (LeafUpdateDelta?, InternalNode)>(node.Stem.Bytes, (null, node)));
                checkStem = false;
            }

            // Recursively calculate the delta
            FrE sectionDelta = newSection != null
                ? CommitBulkRecursive(newSection.AsSpan(), depth + 1)
                : CommitBulkRecursive(leafDeltas[sectionStart..newSectionStart], depth + 1);

            if (newSection != null)
            {
                newSection.Dispose();
                newSection = null;
            }

            delta += Committer.ScalarMul(sectionDelta, sectionStartByte);
        }

        if (node == null || node?.IsBranchNode == true)
        {
            node ??= new InternalNode(VerkleNodeType.BranchNode);
            FrE deltaFre = node.UpdateCommitment(delta);
            SetInternalNode(key, node);
            return deltaFre;
        }
        else
        {
            // It must stem node that need to be split to branch here.
            InternalNode originalStem = node!;
            node = new InternalNode(VerkleNodeType.BranchNode);
            FrE deltaFre = node.UpdateCommitment(delta);
            SetInternalNode(key, node);

            // Need to subtract the original stem commitment
            deltaFre -= originalStem.InternalCommitment.PointAsField;

            return deltaFre;
        }
    }
}
