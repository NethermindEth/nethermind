// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Nethermind.Consensus.IndexTables;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;

namespace Nethermind.Benchmarks.Core;

/// <summary>
/// Benchmarks for EIP-8304 index table entry generation, root calculation, multi-level merging,
/// and RPC SSZ Merkle proof generation.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(InProcessConfig))]
public class IndexTableBenchmarks
{
    private sealed class InProcessConfig : ManualConfig
    {
        public InProcessConfig() => AddJob(Job.ShortRun.WithToolchain(InProcessNoEmitToolchain.Instance));
    }

    private BlockHeader _header = null!;
    private Transaction[] _txs50 = null!;
    private TxReceipt[] _receipts50 = null!;
    private Transaction[] _txs200 = null!;
    private TxReceipt[] _receipts200 = null!;

    private List<IndexEntry> _entries200 = null!;
    private List<IndexEntry> _entries1000 = null!;

    private List<IReadOnlyList<IndexEntry>> _subTablesLevel0 = null!;
    private List<IReadOnlyList<IndexEntry>> _subTablesLevel1 = null!;

    private List<int> _batchQueryIndices = null!;

    [GlobalSetup]
    public void Setup()
    {
        _header = Build.A.BlockHeader.WithNumber(100).TestObject;
        _header.ParentHash = TestItem.KeccakA;

        (_txs50, _receipts50) = CreateTxsAndReceipts(50);
        (_txs200, _receipts200) = CreateTxsAndReceipts(200);

        List<IndexEntry> temp200 = [];
        IndexEntryGenerator.GenerateEntries(_header, _txs50, _receipts50, _header.ParentHash, temp200);
        temp200.Sort();
        _entries200 = temp200;

        List<IndexEntry> temp1000 = [];
        IndexEntryGenerator.GenerateEntries(_header, _txs200, _receipts200, _header.ParentHash, temp1000);
        temp1000.Sort();
        _entries1000 = temp1000;

        // Create 4 sub-tables for merging
        _subTablesLevel0 = new(4);
        for (int i = 0; i < 4; i++)
        {
            List<IndexEntry> sub = [];
            IndexEntryGenerator.GenerateEntries(_header, _txs50, _receipts50, _header.ParentHash, sub);
            sub.Sort();
            _subTablesLevel0.Add(sub);
        }

        _subTablesLevel1 = new(4);
        for (int i = 0; i < 4; i++)
        {
            List<IndexEntry> merged = IndexTableMerger.Merge(_subTablesLevel0);
            _subTablesLevel1.Add(merged);
        }

        _batchQueryIndices = [0, 10, 25, 50, 100, 150];
    }

    private static (Transaction[], TxReceipt[]) CreateTxsAndReceipts(int count)
    {
        Transaction[] txs = new Transaction[count];
        TxReceipt[] receipts = new TxReceipt[count];
        Random random = new(42);

        for (int i = 0; i < count; i++)
        {
            byte[] txHashBytes = new byte[32];
            random.NextBytes(txHashBytes);
            Hash256 txHash = new(txHashBytes);

            byte[] addrBytes = new byte[20];
            random.NextBytes(addrBytes);
            Address addr = new(addrBytes);

            byte[] topicBytes = new byte[32];
            random.NextBytes(topicBytes);
            Hash256 topic0 = new(topicBytes);

            LogEntry log = new(addr, [], [topic0]);

            txs[i] = Build.A.Transaction.WithHash(txHash).TestObject;
            receipts[i] = Build.A.Receipt.WithLogs(log).TestObject;
        }

        return (txs, receipts);
    }

    [Benchmark]
    public List<IndexEntry> GenerateEntries_50Txs()
    {
        List<IndexEntry> entries = [];
        IndexEntryGenerator.GenerateEntries(_header, _txs50, _receipts50, _header.ParentHash, entries);
        return entries;
    }

    [Benchmark]
    public List<IndexEntry> GenerateEntries_200Txs()
    {
        List<IndexEntry> entries = [];
        IndexEntryGenerator.GenerateEntries(_header, _txs200, _receipts200, _header.ParentHash, entries);
        return entries;
    }

    [Benchmark]
    public UInt256 SortAndComputeRoot_200Entries()
    {
        List<IndexEntry> copy = [.. _entries200];
        copy.Sort();
        return IndexTableRootCalculator.ComputeRoot(copy);
    }

    [Benchmark]
    public UInt256 SortAndComputeRoot_1000Entries()
    {
        List<IndexEntry> copy = [.. _entries1000];
        copy.Sort();
        return IndexTableRootCalculator.ComputeRoot(copy);
    }

    [Benchmark]
    public List<IndexEntry> MergeSubTables_Level1() => IndexTableMerger.Merge(_subTablesLevel0);

    [Benchmark]
    public List<IndexEntry> MergeSubTables_Level2() => IndexTableMerger.Merge(_subTablesLevel1);

    [Benchmark]
    public IndexEntryProof GenerateProof_Single() => IndexProofEngine.GenerateProof(_entries1000, 50);

    [Benchmark]
    public IndexEntryProof[] GenerateProofs_Batch() => IndexProofEngine.GenerateProofs(_entries1000, _batchQueryIndices);
}
