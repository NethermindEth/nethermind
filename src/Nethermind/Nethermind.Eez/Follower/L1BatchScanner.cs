// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Eez.Follower;

/// <summary>A batch mined on L1 and what L1 settled of it.</summary>
public readonly record struct ScannedBatch(L1Batch Batch, L1Settlement Settlement);

/// <summary>An L1 block that settled our rollup: its batch logs, in transaction order, and the roots it emitted for us.</summary>
public sealed record L1SettlingBlock(ulong Number, Hash256 Hash, EezL1Log[] BatchLogs, SettledRoot[] Roots);

/// <summary>Finds what L1 settled for our rollup.</summary>
public interface IL1BatchScanner
{
    /// <returns>The blocks from <paramref name="fromBlock"/> to <paramref name="toBlock"/> that settled our rollup, in L1 order.</returns>
    /// <exception cref="L1SourceIncompleteException">The reads disagree or the node cannot serve them yet; retry the range.</exception>
    Task<L1SettlingBlock[]> FindSettlingBlocks(ulong fromBlock, ulong toBlock, CancellationToken token);

    /// <returns>The batches of <paramref name="block"/> and what L1 settled of each, in L1 order.</returns>
    /// <exception cref="L1SourceIncompleteException">L1 reorganized the block or cannot serve its transactions yet.</exception>
    Task<ScannedBatch[]> Scan(L1SettlingBlock block, CancellationToken token);
}

/// <summary>
/// Finds batches by the registry's <c>BatchPosted</c> logs and settlements by our rollup's <c>L2ExecutionPerformed</c>
/// logs; receipts are never read. Logs are read for a whole range, but a batch's calldata only for a block that settled
/// our rollup, one block at a time, so the batches of other rollups are never fetched.
/// </summary>
public sealed class L1BatchScanner(IEezL1Api l1, Address registry, ulong rollupId, ILogManager logManager) : IL1BatchScanner
{
    public static readonly Hash256 BatchPostedTopic = Keccak.Compute("BatchPosted(uint256)");
    public static readonly Hash256 L2ExecutionPerformedTopic = Keccak.Compute("L2ExecutionPerformed(uint64,bytes32)");

    private readonly Hash256 _rollupTopic = RollupTopic(rollupId);
    private readonly ILogger _logger = logManager.GetClassLogger<L1BatchScanner>();

    /// <summary>The indexed <c>rollupId</c> topic of our rollup's <c>L2ExecutionPerformed</c> logs.</summary>
    public static Hash256 RollupTopic(ulong rollupId) => new(new UInt256(rollupId).ToBigEndian());

    public async Task<L1SettlingBlock[]> FindSettlingBlocks(ulong fromBlock, ulong toBlock, CancellationToken token)
    {
        List<EezL1Log> rootLogs = await l1.GetAllLogs(registry, L2ExecutionPerformedTopic, _rollupTopic, fromBlock, toBlock, token);
        if (rootLogs.Count == 0)
        {
            return [];
        }

        List<EezL1Log> batchLogs = await l1.GetAllLogs(registry, BatchPostedTopic, null, fromBlock, toBlock, token);
        rootLogs.Sort(Compare);
        batchLogs.Sort(Compare);

        List<L1SettlingBlock> blocks = [];
        int batch = 0;
        int root = 0;
        while (root < rootLogs.Count)
        {
            EezL1Log first = rootLogs[root];
            List<SettledRoot> roots = [];
            for (; root < rootLogs.Count && rootLogs[root].BlockNumber == first.BlockNumber; root++)
            {
                EezL1Log log = rootLogs[root];
                EnsureOnFork(log, first.BlockHash);
                if (log.Data is { Length: 32 })
                {
                    roots.Add(new SettledRoot(log.BlockNumber, log.BlockHash, log.TransactionIndex, log.LogIndex, new ValueHash256(log.Data)));
                }
            }

            while (batch < batchLogs.Count && batchLogs[batch].BlockNumber < first.BlockNumber)
            {
                batch++;
            }

            int blockStart = batch;
            for (; batch < batchLogs.Count && batchLogs[batch].BlockNumber == first.BlockNumber; batch++)
            {
                EnsureOnFork(batchLogs[batch], first.BlockHash);
            }

            blocks.Add(new L1SettlingBlock(first.BlockNumber, first.BlockHash, CollectionsMarshal.AsSpan(batchLogs)[blockStart..batch].ToArray(), [.. roots]));
        }

        return [.. blocks];
    }

    public async Task<ScannedBatch[]> Scan(L1SettlingBlock block, CancellationToken token)
    {
        List<L1Batch> batches = new(block.BatchLogs.Length);
        foreach (EezL1Log log in block.BatchLogs)
        {
            if (batches.Count > 0 && batches[^1].TransactionHash == log.TransactionHash)
            {
                continue;
            }

            if (await Batch(log, token) is { } scanned)
            {
                batches.Add(scanned);
            }
        }

        L1Settlement[] settlements = SettlementAttribution.Attribute(batches, block.Roots);
        ScannedBatch[] result = new ScannedBatch[batches.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new ScannedBatch(batches[i], settlements[i]);
        }

        return result;
    }

    /// <summary>
    /// The batch a <c>BatchPosted</c> log was emitted for, or <see langword="null"/> when its transaction is not a direct
    /// <c>postAndVerifyBatch</c> call. Such a transaction is skipped even when our rollup settled in it: anyone can wrap a
    /// batch of their own with an <c>executeL2Txs</c> for our rollup, so failing on it would let them stop every follower.
    /// Its roots stay in the window of the batch that queued them.
    /// </summary>
    private async Task<L1Batch?> Batch(EezL1Log log, CancellationToken token)
    {
        EezL1Transaction transaction = await l1.GetTransactionByBlockHashAndIndex(log.BlockHash, log.TransactionIndex, token)
            ?? throw new L1SourceIncompleteException(log.BlockNumber, $"L1 block {log.BlockHash} has no transaction {log.TransactionIndex}.");
        if (transaction.Hash != log.TransactionHash)
        {
            throw new L1SourceIncompleteException(log.BlockNumber, $"Transaction {log.TransactionIndex} of L1 block {log.BlockHash} is not {log.TransactionHash}: L1 reorganized during the scan.");
        }

        PostBatch decoded;
        try
        {
            decoded = EezCalldata.DecodePostAndVerifyBatch(transaction.Input);
        }
        catch (EezAbiException e)
        {
            if (_logger.IsDebug) _logger.Debug($"Skipping batch {log.TransactionHash} in L1 block {log.BlockNumber}, which is not a postAndVerifyBatch call: {e.Message}");
            return null;
        }

        return L1Batch.Of(decoded, rollupId, log.BlockNumber, log.BlockHash, log.TransactionHash, log.TransactionIndex);
    }

    /// <exception cref="L1SourceIncompleteException">The logs of one block number come from two forks of it: the reads straddled a reorg.</exception>
    private static void EnsureOnFork(in EezL1Log log, Hash256 blockHash)
    {
        if (log.BlockHash != blockHash)
        {
            throw new L1SourceIncompleteException(log.BlockNumber, $"The logs of L1 block {log.BlockNumber} come from two forks of it.");
        }
    }

    private static int Compare(EezL1Log a, EezL1Log b) =>
        a.BlockNumber != b.BlockNumber ? a.BlockNumber.CompareTo(b.BlockNumber)
        : a.TransactionIndex != b.TransactionIndex ? a.TransactionIndex.CompareTo(b.TransactionIndex)
        : a.LogIndex.CompareTo(b.LogIndex);
}

/// <summary>The follower cannot go on: L1 settled something it cannot derive.</summary>
public sealed class EezFollowerException(string message, Exception? innerException = null) : Exception(message, innerException);
