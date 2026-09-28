// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Eez.Follower;

/// <summary>A batch mined on L1 and what L1 settled of it.</summary>
public readonly record struct ScannedBatch(L1Batch Batch, L1Settlement Settlement);

/// <summary>
/// Finds the batches mined in a range of L1 blocks and what L1 settled of each. Batches are found by the registry's
/// <c>BatchPosted</c> logs and settlements by our rollup's <c>L2ExecutionPerformed</c> logs; receipts are never read.
/// </summary>
public sealed class L1BatchScanner(IEezL1Api l1, Address registry, ulong rollupId, ILogManager logManager)
{
    public static readonly Hash256 BatchPostedTopic = Keccak.Compute("BatchPosted(uint256)");
    public static readonly Hash256 L2ExecutionPerformedTopic = Keccak.Compute("L2ExecutionPerformed(uint64,bytes32)");

    private readonly Hash256 _rollupTopic = RollupTopic(rollupId);
    private readonly ILogger _logger = logManager.GetClassLogger<L1BatchScanner>();

    /// <summary>The indexed <c>rollupId</c> topic of our rollup's <c>L2ExecutionPerformed</c> logs.</summary>
    public static Hash256 RollupTopic(ulong rollupId) => new(new UInt256(rollupId).ToBigEndian());

    /// <returns>The batches of blocks <paramref name="fromBlock"/> to <paramref name="toBlock"/>, in L1 order.</returns>
    /// <exception cref="L1SourceIncompleteException">The reads disagree or the node cannot serve them yet; retry the range.</exception>
    /// <exception cref="EezFollowerException">A batch that settled our rollup cannot be decoded.</exception>
    public async Task<ScannedBatch[]> Scan(ulong fromBlock, ulong toBlock)
    {
        List<EezL1Log> batchLogs = await Logs(BatchPostedTopic, null, fromBlock, toBlock);
        List<EezL1Log> rootLogs = await Logs(L2ExecutionPerformedTopic, _rollupTopic, fromBlock, toBlock);

        HashSet<(Hash256 Block, Hash256 Transaction)> settledUs = new(rootLogs.Count);
        List<SettledRoot> roots = new(rootLogs.Count);
        foreach (EezL1Log log in rootLogs)
        {
            settledUs.Add((log.BlockHash, log.TransactionHash));
            if (log.Data is { Length: 32 })
            {
                roots.Add(new SettledRoot(log.BlockNumber, log.BlockHash, log.TransactionIndex, log.LogIndex, new ValueHash256(log.Data)));
            }
        }

        batchLogs.Sort(static (a, b) => a.BlockNumber != b.BlockNumber ? a.BlockNumber.CompareTo(b.BlockNumber) : a.TransactionIndex.CompareTo(b.TransactionIndex));
        List<L1Batch> batches = new(batchLogs.Count);
        foreach (EezL1Log log in batchLogs)
        {
            if (batches.Count > 0 && batches[^1].BlockHash == log.BlockHash && batches[^1].TransactionHash == log.TransactionHash)
            {
                continue;
            }

            if (await Batch(log, settledUs.Contains((log.BlockHash, log.TransactionHash))) is { } batch)
            {
                batches.Add(batch);
            }
        }

        L1Settlement[] settlements = SettlementAttribution.Attribute(batches, roots);
        ScannedBatch[] scanned = new ScannedBatch[batches.Count];
        for (int i = 0; i < scanned.Length; i++)
        {
            scanned[i] = new ScannedBatch(batches[i], settlements[i]);
        }

        return scanned;
    }

    private async Task<L1Batch?> Batch(EezL1Log log, bool settledUs)
    {
        EezL1Transaction transaction = await l1.GetTransactionByBlockHashAndIndex(log.BlockHash, log.TransactionIndex)
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
        catch (EezAbiException e) when (!settledUs)
        {
            if (_logger.IsWarn) _logger.Warn($"Skipping batch {log.TransactionHash} in L1 block {log.BlockNumber}, which does not decode and did not settle rollup {rollupId}: {e.Message}");
            return null;
        }
        catch (EezAbiException e)
        {
            throw new EezFollowerException($"Batch {log.TransactionHash} in L1 block {log.BlockNumber} settled rollup {rollupId} but does not decode: {e.Message}", e);
        }

        return L1Batch.Of(decoded, rollupId, log.BlockNumber, log.BlockHash, log.TransactionHash, log.TransactionIndex);
    }

    /// <summary>Reads the logs of a range, halving it while the node refuses it.</summary>
    private async Task<List<EezL1Log>> Logs(Hash256 topic0, Hash256? topic1, ulong fromBlock, ulong toBlock)
    {
        List<EezL1Log> logs = [];
        Stack<(ulong From, ulong To)> ranges = new();
        ranges.Push((fromBlock, toBlock));
        while (ranges.TryPop(out (ulong From, ulong To) range))
        {
            EezL1Log[]? chunk = await l1.GetLogs(registry, topic0, topic1, range.From, range.To);
            if (chunk is not null)
            {
                logs.AddRange(chunk);
                continue;
            }

            if (range.From == range.To)
            {
                throw new L1SourceIncompleteException(range.From, $"L1 refuses the logs of block {range.From}.");
            }

            ulong middle = range.From + (range.To - range.From) / 2;
            ranges.Push((middle + 1, range.To));
            ranges.Push((range.From, middle));
        }

        return logs;
    }
}

/// <summary>The follower cannot go on: L1 settled something it cannot derive.</summary>
public sealed class EezFollowerException(string message, Exception? innerException = null) : Exception(message, innerException);
