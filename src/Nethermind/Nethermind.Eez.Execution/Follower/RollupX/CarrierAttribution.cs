// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Settlement;

namespace Nethermind.Eez.Follower.RollupX;

/// <summary>
/// What a RollupX batch claims for our rollup: the first update's current root and, per update in entry order, its
/// new root, whether its entry persists on L1 and whether it produces an L2 effect.
/// </summary>
/// <param name="Persists">The entry succeeds and L1 did not skip it, so its frame and log persist.</param>
/// <param name="Produces">
/// The update stands for an L2 effect: every update past the anchor except an inbound lookup, which changes no L2 state.
/// </param>
public sealed record ClaimedChain(ValueHash256? CurrentRoot, ValueHash256[] Roots, bool[] Persists, bool[] Produces)
{
    /// <param name="skippedEntries">The entry indexes L1 reported skipped with <c>L2TxSkipped</c>.</param>
    public static ClaimedChain Of(PostBatch batch, ulong rollupId, IReadOnlySet<ulong> skippedEntries)
    {
        ValueHash256? current = null;
        List<ValueHash256> roots = [];
        List<bool> persists = [];
        List<bool> produces = [];
        for (int index = 0; index < batch.Entries.Length; index++)
        {
            ExecutionEntry entry = batch.Entries[index];
            bool lookup = IsLookupOnlyInbound(entry, rollupId);
            foreach (RollupUpdate update in entry.RollupUpdates)
            {
                if (update.RollupId != rollupId)
                {
                    continue;
                }

                produces.Add(roots.Count > 0 && !lookup);
                current ??= update.CurrentRoot;
                roots.Add(update.NewRoot);
                persists.Add(entry.Success && !skippedEntries.Contains((ulong)index));
            }
        }

        return new ClaimedChain(current, [.. roots], [.. persists], [.. produces]);
    }

    /// <summary>
    /// Whether <paramref name="entry"/> is an L1-origin lookup: a no-op update of our rollup with no calls of its own,
    /// whose rolling hash is exactly the seed of that update, so no other entry can pass for one.
    /// </summary>
    public static bool IsLookupOnlyInbound(ExecutionEntry entry, ulong rollupId) =>
        entry.RollupUpdates is [{ } update]
        && entry.ProxyEntryHash != default
        && entry.Calls.Length == 0
        && entry.ExpectedCalls.Length == 0
        && entry.DestinationRollupId == rollupId
        && update.RollupId == rollupId
        && update.CurrentRoot == update.NewRoot
        && update.EtherDelta.IsZero
        && entry.RollingHash == RollingHash.SeedL1(update, entry.ProxyEntryHash);

    /// <summary>A batch that only moves our root onto itself, which competing same-block batches may supersede.</summary>
    public bool IsAnchorOnlyNoOp => Roots.Length == 1 && CurrentRoot == Roots[0];
}

/// <summary>
/// Credits a RollupX batch with the steps L1 ran of it. Only some claimed updates can emit an
/// <c>L2ExecutionPerformed</c>: a failed entry reverts its frame, so its effect is carried by the preceding update with
/// the same root, and a lookup's log may or may not survive its source frame. The claimed chain is folded into these
/// carriers and the observed roots are located as a consecutive run of them, by position, letting optional lookup
/// carriers be passed over.
/// </summary>
public static class CarrierAttribution
{
    /// <returns>
    /// The run L1 settled in the virtual <c>[anchor | effects...]</c> index space, or <see cref="L1Settlement.None"/> when
    /// <paramref name="observed"/> is no run of the claimed carriers.
    /// </returns>
    public static L1Settlement Attribute(ClaimedChain claim, IReadOnlyList<ValueHash256> observed)
    {
        Carrier[] carriers = Carriers(claim);
        if (observed.Count == 0 || carriers.Length == 0 || observed.Count > carriers.Length)
        {
            return L1Settlement.None;
        }

        if (Locate(carriers, observed) is not ({ } first, { } last))
        {
            return L1Settlement.None;
        }

        (int start, int length) = carriers[first].ClaimedIndex == 0
            ? (0, 1 + carriers[last].EffectEnd)
            : (carriers[first].EffectStart + 1, carriers[last].EffectEnd - carriers[first].EffectStart);
        ValueHash256 entry = carriers[first].ClaimedIndex == 0 ? claim.CurrentRoot ?? default : claim.Roots[carriers[first].ClaimedIndex - 1];
        return new L1Settlement(start, length, observed[^1], entry);
    }

    /// <summary>
    /// The earliest consecutive run of carriers that emits <paramref name="observed"/>: <c>starts[i]</c> is the
    /// earliest start of a run that consumed the roots so far and visits carrier <c>i</c> next.
    /// </summary>
    /// <returns>The first and last carrier of the run.</returns>
    private static (int First, int Last)? Locate(Carrier[] carriers, IReadOnlyList<ValueHash256> observed)
    {
        int?[] starts = new int?[carriers.Length + 1];
        for (int i = 0; i < starts.Length; i++)
        {
            starts[i] = i;
        }

        foreach (ValueHash256 root in observed)
        {
            int?[] next = new int?[carriers.Length + 1];
            for (int i = 0; i < carriers.Length; i++)
            {
                if (starts[i] is not { } start)
                {
                    continue;
                }

                if (carriers[i].Optional)
                {
                    starts[i + 1] = starts[i + 1] is { } later ? Math.Min(later, start) : start;
                }

                if (carriers[i].Root == root)
                {
                    next[i + 1] = start;
                }
            }

            starts = next;
        }

        (int First, int Last)? best = null;
        for (int end = 0; end < starts.Length; end++)
        {
            if (starts[end] is { } start && (best is null || start < best.Value.First || (start == best.Value.First && end - 1 < best.Value.Last)))
            {
                best = (start, end - 1);
            }
        }

        return best;
    }

    /// <summary>
    /// The anchor, then each later update that persists or moves the root; each owns the effects up to the next one.
    /// None when the anchor itself claims an effect or the claim is malformed.
    /// </summary>
    private static Carrier[] Carriers(ClaimedChain claim)
    {
        ValueHash256[] roots = claim.Roots;
        if (roots.Length == 0 || roots.Length != claim.Persists.Length || roots.Length != claim.Produces.Length || claim.Produces[0])
        {
            return [];
        }

        int[] effectEnds = new int[roots.Length];
        int effects = 0;
        for (int i = 0; i < roots.Length; i++)
        {
            effects += claim.Produces[i] ? 1 : 0;
            effectEnds[i] = effects;
        }

        List<int> starts = [];
        for (int i = 1; i < roots.Length; i++)
        {
            if (claim.Persists[i] || roots[i] != roots[i - 1])
            {
                starts.Add(i - 1);
            }
        }

        Carrier[] carriers = new Carrier[1 + starts.Count];
        carriers[0] = new Carrier(0, roots[0], 0, starts.Count > 0 ? effectEnds[starts[0]] : effects, false);
        for (int position = 0; position < starts.Count; position++)
        {
            int index = starts[position];
            int end = position + 1 < starts.Count ? effectEnds[starts[position + 1]] : effects;
            carriers[position + 1] = new Carrier(index + 1, roots[index + 1], effectEnds[index], end, !claim.Produces[index + 1]);
        }

        return carriers;
    }

    /// <param name="Optional">A lookup, whose log may roll back with its source frame.</param>
    private readonly record struct Carrier(int ClaimedIndex, ValueHash256 Root, int EffectStart, int EffectEnd, bool Optional);
}
