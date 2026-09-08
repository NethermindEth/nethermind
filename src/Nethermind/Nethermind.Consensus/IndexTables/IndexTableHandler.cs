// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core;
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
public class IndexTableHandler(ITransactionProcessor processor, IIndexTableStore store) : IIndexTableHandler
{
    /// <inheritdoc />
    public void CommitIndexTableRoots(Block block, TxReceipt[] receipts, IReleaseSpec spec, ITxTracer tracer)
    {
        if (!spec.IsEip8304Enabled)
            return;

        // Phase 1: Generate and store level-0 entries, publish immediately
        List<IndexEntry> entries = [];
        IndexEntryGenerator.GenerateEntries(
            block.Header,
            block.Transactions,
            receipts,
            block.Header.ParentHash,
            entries);

        entries.Sort();
        store.Store(0, (long)block.Number, entries, block.Hash);

        UInt256 tableRoot = IndexTableRootCalculator.ComputeRoot(entries);
        ExecuteSystemCall(block, spec, tracer, (long)block.Number, tableSize: 1, tableRoot);

        // Phase 4: Check and publish higher-level tables
        IndexTableMergeScheduler.GetTablesForBlock((long)block.Number, (level, firstBlock, tableSize) =>
        {
            // The system call mutates contract storage, so skipping it would silently produce a
            // state root that diverges from nodes holding the table. Fail loudly instead.
            List<IndexEntry> merged = BuildTable(level, firstBlock)
                ?? throw new InvalidOperationException(
                    $"Cannot build the EIP-8304 level-{level} index table for blocks {firstBlock}-{firstBlock + tableSize - 1}: " +
                    $"index entries are missing. At least {Eip8304Constants.SyncRecoveryBlocks} blocks of index history must be retained.");

            store.Store(level, firstBlock, merged, block.Hash);

            UInt256 higherRoot = IndexTableRootCalculator.ComputeRoot(merged);
            ExecuteSystemCall(block, spec, tracer, firstBlock, tableSize, higherRoot);
        });
    }

    /// <summary>
    /// Builds a level-<paramref name="level"/> table by merging the sub-tables below it.
    /// </summary>
    /// <remarks>
    /// A sub-table that is absent from the store — evicted from its ring buffer, or never published
    /// because this node started mid-range — is rebuilt from the level beneath it, since a table is
    /// fully determined by the level-0 tables it covers. Reconstruction bottoms out at level 0,
    /// which can only come from block processing.
    /// </remarks>
    /// <returns>The merged entries, or <c>null</c> if a level-0 table in the range is unavailable.</returns>
    private List<IndexEntry>? BuildTable(int level, long firstBlock)
    {
        int subLevel = level - 1;
        int subTableSize = Eip8304Constants.TableSizes[subLevel];
        List<IReadOnlyList<IndexEntry>> sources = new(Eip8304Constants.SubTablesPerTable);

        for (int i = 0; i < Eip8304Constants.SubTablesPerTable; i++)
        {
            long subFirst = firstBlock + ((long)i * subTableSize);
            IReadOnlyList<IndexEntry>? subEntries = store.Get(subLevel, subFirst)
                ?? (subLevel > 0 ? BuildTable(subLevel, subFirst) : null);

            if (subEntries is null)
                return null;

            sources.Add(subEntries);
        }

        return IndexTableMerger.Merge(sources);
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
