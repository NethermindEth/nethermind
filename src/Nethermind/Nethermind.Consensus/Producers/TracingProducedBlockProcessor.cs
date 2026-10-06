// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Producers;

/// <summary>Dumps diagnostic traces of the blocks a block producer environment builds, as selected by
/// <see cref="IMiningConfig.DumpProducedBlocks"/>.</summary>
/// <remarks>
/// The dump tracers run in the same pass as the build, next to the caller's tracer, so the block is not executed twice.
/// Candidate transactions that fail to execute are traced too but are not in the produced block, so the traces are
/// filtered to the block's transactions and tagged with their position and the block hash once the block is processed.
/// Files are named after the hash of the processed block, which on sealing chains differs from the sealed hash.
/// Every build attempt is dumped, including each payload improvement; only the newest
/// <see cref="MaxDumpFiles"/> files in <see cref="DumpDirectory"/> are kept.
/// </remarks>
internal sealed class TracingProducedBlockProcessor(
    IBlockchainProcessor processor,
    IMiningConfig miningConfig,
    ISpecProvider specProvider,
    ILogManager logManager) : IBlockchainProcessor
{
    internal static readonly string DefaultDumpDirectory = Path.Combine(Path.GetTempPath(), "nethermind-produced-blocks");

    private readonly DumpOptions _dumpOptions = miningConfig.DumpProducedBlocks;
    private readonly ILogger _logger = logManager.GetClassLogger<TracingProducedBlockProcessor>();

    internal string DumpDirectory { get; init; } = DefaultDumpDirectory;

    internal int MaxDumpFiles { get; init; } = 256;

    public Block? Process(Block block, ProcessingOptions options, IBlockTracer tracer, CancellationToken token = default)
    {
        if (_dumpOptions == DumpOptions.None)
        {
            return processor.Process(block, options, tracer, token);
        }

        BlockReceiptsTracer? receiptsTracer = (_dumpOptions & DumpOptions.Receipts) != 0 ? new BlockReceiptsTracer() : null;
        ParityLikeBlockTracer? parityTracer = (_dumpOptions & DumpOptions.Parity) != 0
            ? new ParityLikeBlockTracer(ParityTraceTypes.StateDiff | ParityTraceTypes.Trace, specProvider)
            : null;
        GethLikeBlockMemoryTracer? gethTracer = (_dumpOptions & DumpOptions.Geth) != 0
            ? new GethLikeBlockMemoryTracer(new GethTraceOptions { EnableMemory = true }, specProvider)
            : null;

        CompositeBlockTracer compositeTracer = new();
        compositeTracer.Add(tracer);
        if (receiptsTracer is not null) compositeTracer.Add(new ReceiptsCollector(receiptsTracer));
        if (parityTracer is not null) compositeTracer.Add(parityTracer);
        if (gethTracer is not null) compositeTracer.Add(gethTracer);

        Block? processed = processor.Process(block, options, compositeTracer, token);
        if (processed is not null)
        {
            Dump(processed, receiptsTracer, parityTracer, gethTracer);
        }

        return processed;
    }

    private void Dump(Block processed, BlockReceiptsTracer? receiptsTracer, ParityLikeBlockTracer? parityTracer, GethLikeBlockMemoryTracer? gethTracer)
    {
        // A failed dump must not fail block production.
        try
        {
            Directory.CreateDirectory(DumpDirectory);
            string name = $"{processed.Number}_{processed.Hash}";

            if ((_dumpOptions & (DumpOptions.Rlp | DumpOptions.RlpLog)) != 0)
            {
                byte[] rlp = Rlp.Encode(processed, RlpBehaviors.AllowExtraBytes).Bytes;
                if ((_dumpOptions & DumpOptions.Rlp) != 0) File.WriteAllBytes(Path.Combine(DumpDirectory, $"block_{name}.rlp"), rlp);
                if ((_dumpOptions & DumpOptions.RlpLog) != 0 && _logger.IsInfo) _logger.Info($"RLP dump of produced block {processed.Hash} is {rlp.ToHexString()}");
            }

            Dictionary<Hash256, int> positions = new(processed.Transactions.Length);
            for (int i = 0; i < processed.Transactions.Length; i++)
            {
                positions[processed.Transactions[i].Hash!] = i;
            }

            if (receiptsTracer is not null)
            {
                TxReceipt[] receipts = Included(receiptsTracer.TxReceipts.ToArray(), positions, static r => r.TxHash, (r, position) =>
                {
                    r.Index = position;
                    r.BlockHash = processed.Hash;
                    r.BlockNumber = processed.Number;
                }).ToArray();
                WriteJson($"receipts_{name}.json", receipts);
            }

            if (parityTracer is not null)
            {
                IReadOnlyCollection<ParityLikeTxTrace> traces = Included(parityTracer.BuildResult(), positions, static t => t.TransactionHash, (t, position) =>
                {
                    t.TransactionPosition = position;
                    t.BlockHash = processed.Hash;
                    t.BlockNumber = processed.Number;
                });
                WriteJson($"parityStyle_{name}.json", traces);
            }

            if (gethTracer is not null)
            {
                IReadOnlyCollection<GethLikeTxTrace> traces = Included(gethTracer.BuildResult(), positions, static t => t.TxHash, static (_, _) => { });
                WriteJson($"gethStyle_{name}.json", traces);
            }

            if (_logger.IsInfo) _logger.Info($"Dumped traces of produced block {processed.ToString(Block.Format.Short)} to {DumpDirectory}");
            DeleteOldestDumps();
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error($"Cannot dump trace of produced block {processed.Hash}", e);
        }
    }

    /// <summary>Keeps the traces of transactions included in the block, ordered by and tagged with their position.</summary>
    private static List<T> Included<T>(IEnumerable<T> traces, Dictionary<Hash256, int> positions, Func<T, Hash256?> getTxHash, Action<T, int> tag)
    {
        List<T> included = new(positions.Count);
        foreach (T trace in traces)
        {
            if (getTxHash(trace) is { } txHash && positions.TryGetValue(txHash, out int position))
            {
                tag(trace, position);
                included.Add(trace);
            }
        }

        return included;
    }

    private void WriteJson<T>(string fileName, T content)
    {
        using FileStream file = new(Path.Combine(DumpDirectory, fileName), FileMode.Create, FileAccess.Write);
        EthereumJsonSerializer.SerializeToStream(file, content, true);
    }

    private void DeleteOldestDumps()
    {
        FileInfo[] files = new DirectoryInfo(DumpDirectory).GetFiles();
        if (files.Length <= MaxDumpFiles) return;

        Array.Sort(files, static (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
        for (int i = MaxDumpFiles; i < files.Length; i++)
        {
            files[i].Delete();
        }
    }

    // The container owns the decorated processor and disposes it.
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Lets a <see cref="BlockReceiptsTracer"/> collect receipts while nested in another block tracer.</summary>
    /// <remarks>
    /// <see cref="BlockReceiptsTracer.StartNewTxTrace"/> hands out the tracer it wraps rather than itself, so nested
    /// under the block processor's own receipts tracer it would never see a transaction's result.
    /// </remarks>
    private sealed class ReceiptsCollector(BlockReceiptsTracer receiptsTracer) : IBlockTracer
    {
        public bool IsTracingRewards => receiptsTracer.IsTracingRewards;

        public void ReportReward(Address author, string rewardType, UInt256 rewardValue) => receiptsTracer.ReportReward(author, rewardType, rewardValue);

        public void StartNewBlockTrace(Block block) => receiptsTracer.StartNewBlockTrace(block);

        public ITxTracer StartNewTxTrace(Transaction? tx)
        {
            receiptsTracer.StartNewTxTrace(tx);
            return receiptsTracer;
        }

        public void EndTxTrace() => receiptsTracer.EndTxTrace();

        public void EndBlockTrace() => receiptsTracer.EndBlockTrace();
    }
}
