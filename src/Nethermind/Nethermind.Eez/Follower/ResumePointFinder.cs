// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;

namespace Nethermind.Eez.Follower;

/// <summary>An L1 block that settled our rollup and the L2 block its settlement ended at.</summary>
public sealed record SettledRecord(L1Anchor L1, BlockHeader L2End, Hash256? DescendantSeed = null)
{
    public ulong L1Number => L1.Number;

    public Hash256 L1Hash => L1.Hash;
}

/// <summary>
/// Finds where the follower resumes without storing anything of its own: walking L1 back from its head, the first
/// block whose settlement ends at a block the local chain still has. Only a block's last settled step counts, since
/// that is the commitment L1 stores after the whole block, and a block can hold several batches for our rollup. The
/// same search recovers from an L1 reorganization, which leaves the last surviving settlement just below it.
/// </summary>
public sealed class ResumePointFinder(IEezL1Api l1, IBlockTree blockTree, Address registry, ulong rollupId, ulong deployBlock, ulong scanBlocks, RollupXSchedule? schedule = null)
{
    private readonly Hash256 _rollupTopic = L1BatchScanner.RollupTopic(rollupId);

    /// <returns>The last settlement the local chain holds, or <see langword="null"/> when none does.</returns>
    /// <exception cref="L1SourceIncompleteException">L1 cannot serve the search yet, or reorganized during it.</exception>
    public async Task<SettledRecord?> Find(ulong l1Head, CancellationToken token)
    {
        ulong to = l1Head;
        while (to >= deployBlock)
        {
            ulong from = to - deployBlock + 1 > scanBlocks ? to - scanBlocks + 1 : deployBlock;
            List<EezL1Log> logs = await l1.GetAllLogs(registry, L1BatchScanner.L2ExecutionPerformedTopic, _rollupTopic, from, to, token);
            if (await LastHeld(logs, token) is { } record)
            {
                return record;
            }

            if (from == deployBlock)
            {
                break;
            }

            to = from - 1;
        }

        return null;
    }

    /// <summary>The latest L1 block among <paramref name="logs"/> whose last settled step is a block of the local chain.</summary>
    private async Task<SettledRecord?> LastHeld(List<EezL1Log> logs, CancellationToken token)
    {
        Dictionary<ulong, EezL1Log> lastPerBlock = [];
        foreach (EezL1Log log in logs)
        {
            if (!lastPerBlock.TryGetValue(log.BlockNumber, out EezL1Log last)
                || log.TransactionIndex > last.TransactionIndex
                || (log.TransactionIndex == last.TransactionIndex && log.LogIndex > last.LogIndex))
            {
                lastPerBlock[log.BlockNumber] = log;
            }
        }

        List<ulong> blocks = [.. lastPerBlock.Keys];
        blocks.Sort();
        for (int i = blocks.Count - 1; i >= 0; i--)
        {
            EezL1Log log = lastPerBlock[blocks[i]];
            if (L1BatchScanner.SettledRootOf(log) is not { } settled || blockTree.FindHeader(new Hash256(settled)) is not { } header || !blockTree.IsMainChain(header))
            {
                continue;
            }

            EezL1Block canonical = await l1.GetBlockByNumber(log.BlockNumber, token)
                ?? throw new L1SourceIncompleteException(log.BlockNumber, $"L1 cannot serve block {log.BlockNumber}.");
            if (canonical.Hash == log.BlockHash)
            {
                Hash256? seed = schedule is null ? null : await DescendantSeed(header, canonical, token);
                return new SettledRecord(canonical.ToAnchor(), header, seed);
            }
        }

        return null;
    }

    private async Task<Hash256> DescendantSeed(BlockHeader endpoint, EezL1Block settling, CancellationToken token)
    {
        RollupXAnchorKind kind = schedule!.AnchorKind(endpoint.Number, settling.Timestamp)
            ?? throw new EezFollowerException($"L2 block {endpoint.Number} cannot be settled in L1 block {settling.Number}.");
        if (kind == RollupXAnchorKind.Live)
        {
            return settling.ToAnchor().MixHash;
        }

        Hash256 seed = endpoint.MixHash ?? throw new EezFollowerException($"L2 block {endpoint.Number} carries no prevRandao.");
        ulong timestamp = schedule.Timestamp(endpoint.Number)!.Value;
        ulong from = schedule.L1Reference.Number;
        ulong to = settling.Number;
        while (from < to)
        {
            ulong middle = from + (to - from) / 2;
            EezL1Block block = await Block(middle, token);
            if (block.Timestamp < timestamp)
            {
                from = middle + 1;
            }
            else
            {
                to = middle;
            }
        }

        EezL1Block liveSlot = await Block(from, token);
        if (liveSlot.Timestamp == timestamp && from >= deployBlock)
        {
            List<EezL1Log> logs = await l1.GetAllLogs(registry, L1BatchScanner.L2ExecutionPerformedTopic, _rollupTopic, from, from, token);
            foreach (EezL1Log log in logs)
            {
                if (log.BlockHash != liveSlot.Hash)
                {
                    throw new L1SourceIncompleteException(from, $"L1 reorganized block {from} during seed recovery.");
                }

                if (L1BatchScanner.SettledRootOf(log) is not { } root || blockTree.FindHeader(new Hash256(root)) is not { } accepted)
                {
                    throw new L1SourceIncompleteException(from, $"The local chain does not hold an accepted endpoint from L1 block {from}; its descendant seed cannot be recovered.");
                }

                if (accepted.Number == endpoint.Number)
                {
                    seed = liveSlot.ToAnchor().MixHash;
                }
            }
        }

        if ((await Block(from, token)).Hash != liveSlot.Hash || (await Block(settling.Number, token)).Hash != settling.Hash)
        {
            throw new L1SourceIncompleteException(settling.Number, "L1 reorganized during descendant seed recovery.");
        }

        return seed;
    }

    private async Task<EezL1Block> Block(ulong number, CancellationToken token) =>
        await l1.GetBlockByNumber(number, token) ?? throw new L1SourceIncompleteException(number, $"L1 cannot serve block {number}.");
}
