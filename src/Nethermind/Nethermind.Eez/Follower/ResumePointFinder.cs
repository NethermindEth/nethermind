// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Follower;

/// <summary>An L1 block that settled our rollup and the L2 block its settlement ended at.</summary>
public sealed record SettledRecord(ulong L1Number, Hash256 L1Hash, BlockHeader L2End);

/// <summary>
/// Finds where the follower resumes without storing anything of its own: walking L1 back from its head, the first
/// block whose settlement ends at a block the local chain still has. Only a block's last settled step counts, since
/// that is the commitment L1 stores after the whole block, and a block can hold several batches for our rollup. The
/// same search recovers from an L1 reorganization, which leaves the last surviving settlement just below it.
/// </summary>
public sealed class ResumePointFinder(IEezL1Api l1, IBlockTree blockTree, Address registry, ulong rollupId, ulong deployBlock, ulong scanBlocks)
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
            if (log.Data is not { Length: 32 } || blockTree.FindHeader(new Hash256(log.Data)) is not { } header || !blockTree.IsMainChain(header))
            {
                continue;
            }

            EezL1Block canonical = await l1.GetBlockByNumber(log.BlockNumber, token)
                ?? throw new L1SourceIncompleteException(log.BlockNumber, $"L1 cannot serve block {log.BlockNumber}.");
            if (canonical.Hash == log.BlockHash)
            {
                return new SettledRecord(log.BlockNumber, log.BlockHash, header);
            }
        }

        return null;
    }
}
