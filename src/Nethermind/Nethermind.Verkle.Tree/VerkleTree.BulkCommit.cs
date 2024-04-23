// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
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

        _ = CalculateDelta(leafDeltas.AsSpan(), 0);

        _leafUpdateCache.Clear();
    }

    private FrE CalculateDelta(ReadOnlySpan<KeyValuePair<byte[], (LeafUpdateDelta?, InternalNode?)>> leafDeltas, int depth)
    {
        var key = leafDeltas[0].Key.AsSpan()[..depth];
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

        bool checkStem = node?.IsStem == true;
        ArrayPoolList<KeyValuePair<byte[], (LeafUpdateDelta?, InternalNode)>>? newSection = null;
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

            if (checkStem)
            {
                int compare = Bytes.BytesComparer.Compare(node.Stem.Bytes, leafDeltas[i].Key);
                if (compare == 0)
                {
                    // It match exactly? Then its an update, so we ignore setting a new one.
                    checkStem = false;
                    InternalNode clonedNode = node.Clone();
                    clonedNode.UpdateCommitment(leafDeltas[i].Value.Item1!.Value);
                    newSection.Add(new KeyValuePair<byte[], (LeafUpdateDelta?, InternalNode)>(leafDeltas[i].Key, (null, clonedNode)));
                }
                else if (compare < 0)
                {
                    // The current stem is less than the leaf update delta, so add the current stem first.
                    newSection.Add(new KeyValuePair<byte[], (LeafUpdateDelta?, InternalNode)>(node.Stem.Bytes, (null, node)));
                    checkStem = false;
                    newSection.Add(leafDeltas[i]);
                }
                else
                {
                    newSection.Add(leafDeltas[i]);
                }
            }
            else
            {
                newSection.Add(leafDeltas[i]);
            }
        }

        {
            AddDelta(leafDeltas.Length, leafDeltas);
        }

        // The current stem is still not set.
        if (checkStem)
        {
            newSection = new ArrayPoolList<KeyValuePair<byte[], (LeafUpdateDelta?, InternalNode)>>(1);
            newSection.Add(new KeyValuePair<byte[], (LeafUpdateDelta?, InternalNode)>(node.Stem.Bytes, (null, node)));
            var recursiveDelta = CalculateDelta(newSection.AsSpan(), depth + 1);
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

            // Get the delta for the previous section
            FrE sectionDelta = newSection != null
                ? CalculateDelta(newSection.AsSpan(), depth + 1)
                : CalculateDelta(leafDeltas[sectionStart..newSectionStart], depth + 1);

            if (newSection != null)
            {
                newSection.Dispose();
                newSection = null;
            }

            delta += Committer.ScalarMul(sectionDelta, sectionStartByte);
        }

        if (node == null || node?.IsBranchNode == true)
        {
            if (node == null) node = new InternalNode(VerkleNodeType.BranchNode);
            var deltaFre = node.UpdateCommitment(delta);
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
            deltaFre -= originalStem.InternalCommitment.PointAsField;

            return deltaFre;
        }
    }
}
