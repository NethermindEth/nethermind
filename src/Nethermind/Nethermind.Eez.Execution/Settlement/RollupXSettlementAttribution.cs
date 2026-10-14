// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Follower.RollupX;

namespace Nethermind.Eez.Execution.Settlement;

internal static class RollupXSettlementAttribution
{
    internal static (L1Settlement[] Settlements, int[] Superseded) Attribute(
        IReadOnlyList<L1Batch> batches, IReadOnlyList<ClaimedChain> claims, IReadOnlyList<SettledRoot> roots)
    {
        L1Settlement[] settlements = AttributeWindows(batches, claims, roots);
        return (settlements, SupersedeNoOps(claims, settlements));
    }

    internal static L1Settlement[] AttributeComplete(
        IReadOnlyList<L1Batch> batches, IReadOnlyList<ClaimedChain> claims, IReadOnlyList<SettledRoot> roots)
    {
        L1Settlement[] settlements = AttributeWindows(batches, claims, roots);
        ulong[] windowEnds = new ulong[batches.Count];
        for (int i = 0; i < batches.Count; i++) windowEnds[i] = SettlementAttribution.WindowEnd(batches, batches[i]);
        foreach (SettledRoot root in roots)
        {
            bool covered = false;
            for (int i = 0; i < batches.Count; i++)
            {
                L1Batch batch = batches[i];
                if (!settlements[i].IsEmpty && root.BlockHash == batch.BlockHash &&
                    root.TransactionIndex >= batch.TransactionIndex && root.TransactionIndex < windowEnds[i])
                {
                    covered = true;
                    break;
                }
            }
            if (!covered) throw new EezSettlementException("settlement roots lack complete batch attribution");
        }
        SupersedeNoOps(claims, settlements);
        return settlements;
    }

    private static L1Settlement[] AttributeWindows(
        IReadOnlyList<L1Batch> batches, IReadOnlyList<ClaimedChain> claims, IReadOnlyList<SettledRoot> roots)
    {
        if (batches.Count != claims.Count) throw new ArgumentException("each batch requires one claim");
        L1Settlement[] settlements = new L1Settlement[batches.Count];
        for (int i = 0; i < batches.Count; i++)
        {
            L1Batch batch = batches[i];
            ulong end = SettlementAttribution.WindowEnd(batches, batch);
            List<ValueHash256> observed = [];
            foreach (SettledRoot root in roots)
            {
                if (root.TransactionIndex >= batch.TransactionIndex && root.TransactionIndex < end && root.BlockHash == batch.BlockHash)
                    observed.Add(root.Root);
            }
            settlements[i] = CarrierAttribution.Attribute(claims[i], observed);
        }
        return settlements;
    }

    private static int[] SupersedeNoOps(IReadOnlyList<ClaimedChain> claims, L1Settlement[] settlements)
    {
        HashSet<ValueHash256> richer = [];
        Dictionary<ValueHash256, int> firstNoOp = [];
        for (int i = 0; i < claims.Count; i++)
        {
            if (settlements[i].IsEmpty || claims[i].CurrentRoot is not { } root) continue;
            if (!claims[i].IsAnchorOnlyNoOp) richer.Add(root);
            else firstNoOp.TryAdd(root, i);
        }
        List<int>? superseded = null;
        for (int i = 0; i < claims.Count; i++)
        {
            if (!settlements[i].IsEmpty && claims[i] is { IsAnchorOnlyNoOp: true, CurrentRoot: { } root } &&
                (richer.Contains(root) || firstNoOp[root] != i))
            {
                settlements[i] = L1Settlement.None;
                (superseded ??= []).Add(i);
            }
        }
        return superseded?.ToArray() ?? [];
    }
}
