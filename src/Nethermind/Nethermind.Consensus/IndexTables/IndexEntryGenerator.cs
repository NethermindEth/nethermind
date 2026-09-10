// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Consensus.IndexTables;

/// <summary>
/// Generates EIP-8304 index entries from block data.
/// </summary>
/// <remarks>
/// For each processed block, this class produces the full set of index entries
/// (block, transaction, log address, and log topic entries) according to the
/// EIP-8304 indexing rules. Block entries use a one-block delay: block N's
/// table contains the block entry for block N−1. No block entry is generated
/// for the genesis block.
/// <para>See <see href="https://eips.ethereum.org/EIPS/eip-8304">EIP-8304</see>.</para>
/// </remarks>
public static class IndexEntryGenerator
{
    /// <summary>The number of log topics EIP-8304 indexes (entry type IDs 3–6).</summary>
    private const int MaxIndexedTopics = 4;

    /// <summary>
    /// Generates all index entries for a single block and appends them to <paramref name="entries"/>.
    /// </summary>
    /// <remarks>
    /// EIP-8304 indexing rules:
    /// <list type="bullet">
    ///   <item>Block entries use a one-block delay: block N adds the entry for block N−1.
    ///         No block entry is added when processing genesis (block 0).</item>
    ///   <item>For each transaction, a transaction entry is created with the cumulative
    ///         log count (total logs in the block before this transaction).</item>
    ///   <item>For each log in each receipt, a log address entry and up to 4 log topic
    ///         entries are created. Log indices are relative to the transaction beginning.</item>
    /// </list>
    /// </remarks>
    /// <param name="header">The block header being processed.</param>
    /// <param name="transactions">The block's transactions.</param>
    /// <param name="receipts">The block's transaction receipts (parallel with transactions).</param>
    /// <param name="parentBlockHash">The hash of the parent block (block N−1). May be null for genesis.</param>
    /// <param name="entries">The list to which generated entries are appended.</param>
    /// <exception cref="ArgumentException">
    /// If <paramref name="receipts"/> is not parallel with <paramref name="transactions"/>.
    /// </exception>
    public static void GenerateEntries(
        BlockHeader header,
        Transaction[] transactions,
        TxReceipt[] receipts,
        Hash256? parentBlockHash,
        IList<IndexEntry> entries)
    {
        if (receipts.Length != transactions.Length)
            throw new ArgumentException(
                $"Expected {transactions.Length} receipts for block {header.Number}, got {receipts.Length}.",
                nameof(receipts));

        // EIP-8304: block entry with one-block delay — block N adds entry for N−1.
        // Genesis (block 0) has no parent block entry.
        if (!header.IsGenesis)
        {
            Hash256 parentHash = parentBlockHash ?? header.ParentHash
                ?? throw new ArgumentException($"Non-genesis block {header.Number} must have a parent block hash.", nameof(parentBlockHash));
            entries.Add(IndexEntry.CreateBlock(parentHash, header.Number - 1));
        }

        uint cumulativeLogCount = 0;

        for (int txIdx = 0; txIdx < transactions.Length; txIdx++)
        {
            Transaction tx = transactions[txIdx];

            // The hash may not be memoized yet on locally built transactions; the entry count
            // (and therefore the table root) must not depend on whether it happens to be cached.
            Hash256 txHash = tx.Hash ?? tx.CalculateHash();
            tx.Hash ??= txHash;
            entries.Add(IndexEntry.CreateTransaction(txHash, header.Number, (uint)txIdx, cumulativeLogCount));

            // Process logs from the receipt
            TxReceipt receipt = receipts[txIdx];
            LogEntry[]? logs = receipt.Logs;

            if (logs is not null)
            {
                for (int logIdx = 0; logIdx < logs.Length; logIdx++)
                {
                    LogEntry log = logs[logIdx];

                    // Log address entry
                    entries.Add(IndexEntry.CreateLogAddress(log.Address, header.Number, (uint)txIdx, (uint)logIdx));

                    // Log topic entries (up to 4 topics per log, type IDs 3–6). The EVM cannot emit
                    // more than 4, but a malformed receipt payload can; extra topics are not indexable.
                    Hash256[] topics = log.Topics;
                    int topicCount = Math.Min(topics.Length, MaxIndexedTopics);
                    for (int topicIdx = 0; topicIdx < topicCount; topicIdx++)
                    {
                        entries.Add(IndexEntry.CreateLogTopic(topicIdx, topics[topicIdx], header.Number, (uint)txIdx, (uint)logIdx));
                    }
                }

                cumulativeLogCount += (uint)logs.Length;
            }
        }
    }
}
