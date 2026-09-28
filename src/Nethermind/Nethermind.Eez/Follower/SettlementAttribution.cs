// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Follower;

/// <summary>
/// Credits each batch with the steps L1 ran for it. Several batches can settle our rollup in one L1 block, so a
/// batch owns only the roots emitted from its own transaction up to the next batch in that block that lists our
/// rollup, which clears our queue on L1. Within that window the roots must be a consecutive run of the batch's
/// claimed chain; roots it never claimed credit it with nothing.
/// </summary>
public static class SettlementAttribution
{
    /// <param name="batches">Every batch found in a range of L1 blocks.</param>
    /// <param name="roots">Every root our rollup settled in the same range.</param>
    /// <returns>The settlement of each batch, in the order of <paramref name="batches"/>.</returns>
    /// <exception cref="L1SourceIncompleteException">A block's roots are all from another fork of it: the reads straddled a reorg.</exception>
    public static L1Settlement[] Attribute(IReadOnlyList<L1Batch> batches, IReadOnlyList<SettledRoot> roots)
    {
        Dictionary<ulong, List<SettledRoot>> rootsByBlock = [];
        foreach (SettledRoot root in roots)
        {
            if (!rootsByBlock.TryGetValue(root.BlockNumber, out List<SettledRoot>? list))
            {
                rootsByBlock[root.BlockNumber] = list = [];
            }

            list.Add(root);
        }

        foreach (List<SettledRoot> list in rootsByBlock.Values)
        {
            list.Sort(static (a, b) => a.TransactionIndex != b.TransactionIndex
                ? a.TransactionIndex.CompareTo(b.TransactionIndex)
                : a.LogIndex.CompareTo(b.LogIndex));
        }

        L1Settlement[] settlements = new L1Settlement[batches.Count];
        for (int i = 0; i < batches.Count; i++)
        {
            L1Batch batch = batches[i];
            if (!rootsByBlock.TryGetValue(batch.BlockNumber, out List<SettledRoot>? blockRoots))
            {
                continue;
            }

            if (!OnFork(blockRoots, batch.BlockHash))
            {
                throw new L1SourceIncompleteException(batch.BlockNumber, $"The settled roots of L1 block {batch.BlockNumber} are from another fork of it.");
            }

            ulong windowEnd = WindowEnd(batches, batch);
            List<ValueHash256> observed = [];
            foreach (SettledRoot root in blockRoots)
            {
                if (root.TransactionIndex >= batch.TransactionIndex && root.TransactionIndex < windowEnd && root.BlockHash == batch.BlockHash)
                {
                    observed.Add(root.Root);
                }
            }

            settlements[i] = Match(batch, observed);
        }

        return settlements;
    }

    private static bool OnFork(List<SettledRoot> roots, Hash256 blockHash)
    {
        foreach (SettledRoot root in roots)
        {
            if (root.BlockHash == blockHash)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The transaction index of the next batch in the same block that lists our rollup.</summary>
    private static ulong WindowEnd(IReadOnlyList<L1Batch> batches, L1Batch batch)
    {
        ulong end = ulong.MaxValue;
        foreach (L1Batch other in batches)
        {
            if (other.VerifiesOurRollup && other.BlockNumber == batch.BlockNumber && other.BlockHash == batch.BlockHash
                && other.TransactionIndex > batch.TransactionIndex && other.TransactionIndex < end)
            {
                end = other.TransactionIndex;
            }
        }

        return end;
    }

    /// <summary>Locates <paramref name="observed"/> as a consecutive run of the claimed chain, by position.</summary>
    private static L1Settlement Match(L1Batch batch, List<ValueHash256> observed)
    {
        ValueHash256[] chain = batch.ClaimedChain;
        if (observed.Count == 0 || observed.Count > chain.Length)
        {
            return L1Settlement.None;
        }

        ReadOnlySpan<ValueHash256> run = CollectionsMarshal.AsSpan(observed);
        for (int start = 0; start <= chain.Length - run.Length; start++)
        {
            if (chain.AsSpan(start, run.Length).SequenceEqual(run))
            {
                ValueHash256 entry = start == 0 ? batch.ClaimedCurrentState ?? default : chain[start - 1];
                return new L1Settlement(start, observed.Count, observed[^1], entry);
            }
        }

        return L1Settlement.None;
    }
}

/// <summary>An L1 read that must be retried: its parts came from different forks, or a node has not caught up.</summary>
public sealed class L1SourceIncompleteException(ulong blockNumber, string message) : Exception(message)
{
    public ulong BlockNumber { get; } = blockNumber;
}
