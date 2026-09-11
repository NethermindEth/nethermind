// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Consensus.IndexTables;

/// <summary>
/// Computes EIP-8304 index table roots and commits them to the system contract.
/// </summary>
/// <remarks>
/// During block processing, generates index entries from the block's transactions
/// and receipts, computes the SSZ table root, and executes a system call to store
/// the root in the index contract. Level 0 tables (single-block, table_size=1) are
/// committed immediately. Higher-level tables (levels 1–4) are committed with a
/// publication delay of <c>TABLE_SIZES[i] / 4</c> blocks, built by merging 4
/// lower-level tables.
/// <para>See <see href="https://eips.ethereum.org/EIPS/eip-8304">EIP-8304</see>.</para>
/// </remarks>
public class IndexTableHandler(
    ITransactionProcessor processor,
    IIndexTableStore store,
    ISpecProvider? specProvider = null,
    IBlockTree? blockTree = null,
    IReceiptStorage? receiptStorage = null) : IIndexTableHandler
{
    private Hash256? _lastCommittedBlockHash;
    private long _lastCommittedBlockNumber;
    private List<IndexEntry>? _lastCommittedEntries;
    private readonly List<(int Level, long FirstBlock, List<IndexEntry> Merged)> _lastCommittedHigherTables = [];

    /// <inheritdoc />
    public void CommitIndexTableRoots(Block block, TxReceipt[] receipts, IReleaseSpec spec, ITxTracer tracer)
    {
        if (!spec.IsEip8304Enabled)
            return;

        _lastCommittedBlockHash = block.Hash;
        _lastCommittedBlockNumber = (long)block.Number;
        _lastCommittedHigherTables.Clear();

        // Phase 1: Generate and store level-0 entries, publish immediately
        List<IndexEntry> entries = [];
        IndexEntryGenerator.GenerateEntries(
            block.Header,
            block.Transactions,
            receipts,
            block.Header.ParentHash,
            entries);

        entries.Sort();
        _lastCommittedEntries = entries;
        store.Store(0, (long)block.Number, entries, block.Hash);

        UInt256 tableRoot = IndexTableRootCalculator.ComputeRoot(entries);
        ExecuteSystemCall(block, spec, tracer, (long)block.Number, tableSize: 1, tableRoot);

        // Phase 4: Check and publish higher-level tables
        Dictionary<long, BlockHeader> ancestorCache = [];
        IndexTableMergeScheduler.GetTablesForBlock((long)block.Number, (level, firstBlock, tableSize) =>
        {
            // The system call mutates contract storage, so skipping it would silently produce a
            // state root that diverges from nodes holding the table. Fail loudly instead.
            List<IndexEntry> merged = BuildTable(level, firstBlock, block.Header, ancestorCache)
                ?? throw new InvalidOperationException(
                    $"Cannot build the EIP-8304 level-{level} index table for blocks {firstBlock}-{firstBlock + tableSize - 1}: " +
                    $"index entries are missing. At least {Eip8304Constants.SyncRecoveryBlocks} blocks of index history must be retained.");

            _lastCommittedHigherTables.Add((level, firstBlock, merged));
            store.Store(level, firstBlock, merged, block.Hash);

            UInt256 higherRoot = IndexTableRootCalculator.ComputeRoot(merged);
            ExecuteSystemCall(block, spec, tracer, firstBlock, tableSize, higherRoot);
        }, firstBlock => IsForkActiveAt(firstBlock, block.Header, ancestorCache));
    }

    /// <inheritdoc />
    public void UpdateFinalBlockHash(Block block)
    {
        if (_lastCommittedEntries is null || (long)block.Number != _lastCommittedBlockNumber)
            return;

        if (block.Hash is not null && block.Hash != _lastCommittedBlockHash)
        {
            if (_lastCommittedBlockHash is not null)
            {
                store.Remove(0, _lastCommittedBlockNumber, _lastCommittedBlockHash);
                foreach ((int level, long firstBlock, _) in _lastCommittedHigherTables)
                {
                    store.Remove(level, firstBlock, _lastCommittedBlockHash);
                }
            }

            store.Store(0, _lastCommittedBlockNumber, _lastCommittedEntries, block.Hash);
            foreach ((int level, long firstBlock, List<IndexEntry> merged) in _lastCommittedHigherTables)
            {
                store.Store(level, firstBlock, merged, block.Hash);
            }

            _lastCommittedBlockHash = block.Hash;
        }
    }

    /// <inheritdoc />
    public void RollbackBlock(Block block)
    {
        store.Remove(0, (long)block.Number, block.Hash);
        if (_lastCommittedBlockHash is not null && _lastCommittedBlockHash != block.Hash && (long)block.Number == _lastCommittedBlockNumber)
        {
            store.Remove(0, (long)block.Number, _lastCommittedBlockHash);
        }

        IndexTableMergeScheduler.GetTablesForBlock((long)block.Number, (level, firstBlock, tableSize) =>
        {
            store.Remove(level, firstBlock, block.Hash);
            if (_lastCommittedBlockHash is not null && _lastCommittedBlockHash != block.Hash && (long)block.Number == _lastCommittedBlockNumber)
            {
                store.Remove(level, firstBlock, _lastCommittedBlockHash);
            }
        });
    }

    private bool IsForkActiveAt(
        long firstBlock,
        BlockHeader currentHeader,
        Dictionary<long, BlockHeader>? ancestorCache = null)
    {
        if (firstBlock < 0)
            return false;

        if (specProvider is null)
            return true;

        BlockHeader? header = FindAncestorHeader(currentHeader, firstBlock, ancestorCache);
        IReleaseSpec targetSpec = header is not null
            ? specProvider.GetSpec(header)
            : specProvider.GetSpec((ulong)firstBlock, null);

        return targetSpec.IsEip8304Enabled;
    }

    /// <summary>
    /// Builds a level-<paramref name="level"/> table by merging the sub-tables below it along the current branch.
    /// </summary>
    /// <remarks>
    /// A sub-table that is absent from the store — evicted from its ring buffer, or never published
    /// because this node started mid-range — is rebuilt from the level beneath it, since a table is
    /// fully determined by the level-0 tables it covers. Reconstruction bottoms out at level 0,
    /// which can be recovered from historical block headers, transactions, and receipts.
    /// </remarks>
    /// <returns>The merged entries, or <c>null</c> if a level-0 table in the range is unavailable.</returns>
    private List<IndexEntry>? BuildTable(
        int level,
        long firstBlock,
        BlockHeader currentHeader,
        Dictionary<long, BlockHeader>? ancestorCache = null)
    {
        int subLevel = level - 1;
        int subTableSize = Eip8304Constants.TableSizes[subLevel];
        List<IReadOnlyList<IndexEntry>> sources = new(Eip8304Constants.SubTablesPerTable);
        ancestorCache ??= [];

        for (int i = 0; i < Eip8304Constants.SubTablesPerTable; i++)
        {
            long subFirst = firstBlock + ((long)i * subTableSize);
            long subLast = subFirst + subTableSize - 1;
            Hash256? branchBlockHash = FindAncestorHash(currentHeader, subLast, ancestorCache);

            IReadOnlyList<IndexEntry>? subEntries = store.Get(subLevel, subFirst, branchBlockHash);
            if (subEntries is null)
            {
                if (subLevel > 0)
                {
                    subEntries = BuildTable(subLevel, subFirst, currentHeader, ancestorCache);
                    if (subEntries is not null)
                    {
                        store.Store(subLevel, subFirst, subEntries, branchBlockHash);
                    }
                }
                else
                {
                    subEntries = RecoverHistoricalEntries(subFirst, branchBlockHash);
                }
            }

            if (subEntries is null)
                return null;

            sources.Add(subEntries);
        }

        return IndexTableMerger.Merge(sources);
    }

    private Hash256? FindAncestorHash(
        BlockHeader currentHeader,
        long targetBlockNumber,
        Dictionary<long, BlockHeader> ancestorCache) =>
        FindAncestorHeader(currentHeader, targetBlockNumber, ancestorCache)?.Hash;

    private BlockHeader? FindAncestorHeader(
        BlockHeader currentHeader,
        long targetBlockNumber,
        Dictionary<long, BlockHeader>? ancestorCache = null)
    {
        if ((long)currentHeader.Number == targetBlockNumber)
            return currentHeader;

        if (targetBlockNumber > (long)currentHeader.Number)
            return null;

        if (ancestorCache is not null && ancestorCache.TryGetValue(targetBlockNumber, out BlockHeader? cached))
            return cached;

        if (blockTree is null)
            return null;

        BlockHeader? current = currentHeader;
        while (current is not null && (long)current.Number > targetBlockNumber)
        {
            if (current.ParentHash is null)
                return null;

            long parentNumber = (long)current.Number - 1;
            if (ancestorCache is not null && ancestorCache.TryGetValue(parentNumber, out BlockHeader? parentInCache))
            {
                current = parentInCache;
                continue;
            }

            BlockHeader? parentHeader = blockTree.FindHeader(current.ParentHash, BlockTreeLookupOptions.None);
            if (parentHeader is not null)
            {
                ancestorCache?.TryAdd((long)parentHeader.Number, parentHeader);
            }

            current = parentHeader;
        }

        return current is not null && (long)current.Number == targetBlockNumber ? current : null;
    }

    private IReadOnlyList<IndexEntry>? RecoverHistoricalEntries(long blockNumber, Hash256? branchBlockHash)
    {
        if (blockTree is null || receiptStorage is null)
            return null;

        Block? histBlock = branchBlockHash is not null
            ? (blockTree.FindBlock(branchBlockHash, BlockTreeLookupOptions.None, (ulong)blockNumber) ?? blockTree.FindBlock((ulong)blockNumber, BlockTreeLookupOptions.None))
            : blockTree.FindBlock((ulong)blockNumber, BlockTreeLookupOptions.None);

        if (histBlock is null)
            return null;

        TxReceipt[]? histReceipts = receiptStorage.Get(histBlock);
        if (histReceipts is null && histBlock.Transactions.Length > 0)
            return null;

        if (histReceipts is not null && histReceipts.Length != histBlock.Transactions.Length)
            return null;

        List<IndexEntry> entries = [];
        IndexEntryGenerator.GenerateEntries(
            histBlock.Header,
            histBlock.Transactions,
            histReceipts ?? [],
            histBlock.Header.ParentHash,
            entries);

        entries.Sort();
        store.Store(0, blockNumber, entries, histBlock.Hash);
        return entries;
    }

    private void ExecuteSystemCall(
        Block block,
        IReleaseSpec spec,
        ITxTracer tracer,
        long firstBlock,
        int tableSize,
        in UInt256 tableRoot)
    {
        Address? contractAddress = spec.Eip8304ContractAddress;
        if (contractAddress is null)
            return;

        byte[] calldata = new byte[Eip8304Constants.CalldataLength];
        Span<byte> calldataSpan = calldata;

        calldataSpan[..32].Clear();
        BinaryPrimitives.WriteInt64BigEndian(calldataSpan[24..], firstBlock);

        calldataSpan.Slice(32, 32).Clear();
        BinaryPrimitives.WriteInt32BigEndian(calldataSpan[60..], tableSize);

        // The table root is an SSZ chunk: its canonical 32 bytes are the little-endian limb
        // layout, not the big-endian numeric rendering of the same value.
        tableRoot.ToLittleEndian(calldataSpan[64..]);

        Transaction transaction = new()
        {
            Value = 0,
            Data = calldata,
            To = contractAddress,
            SenderAddress = Address.SystemUser,
            GasLimit = (long)Eip8304Constants.GasLimit,
            GasPrice = 0,
        };

        transaction.Hash = transaction.CalculateHash();

        processor.Execute(transaction, tracer);
    }
}
