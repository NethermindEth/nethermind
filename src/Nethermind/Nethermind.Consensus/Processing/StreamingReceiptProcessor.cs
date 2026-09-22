// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Metric;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;

namespace Nethermind.Consensus.Processing;

internal sealed class StreamingReceiptProcessor : IDisposable
{
    private readonly Channel<TxReceipt> _receipts = Channel.CreateBounded<TxReceipt>(new BoundedChannelOptions(64)
    {
        SingleReader = true,
        SingleWriter = true,
        AllowSynchronousContinuations = false
    });
    private readonly Task<(Bloom BlockBloom, Hash256 ReceiptsRoot)> _completion;

    internal StreamingReceiptProcessor(int count, IReleaseSpec spec, ReceiptMessageDecoder decoder)
        => _completion = Task.Run(async () =>
        {
            try
            {
                ReceiptTrie.StreamingRoot root = new(count, spec, decoder);
                Bloom bloom = new();
                await foreach (TxReceipt receipt in _receipts.Reader.ReadAllAsync())
                {
                    using (MetricsTimer<BloomTimeSink> _ = new())
                    {
                        receipt.CalculateBloom();
                        bloom.Accumulate(receipt.Bloom!);
                    }
                    using (MetricsTimer<RootTimeSink> _ = new()) root.Append(receipt);
                }
                using (MetricsTimer<RootTimeSink> _ = new()) return (bloom, root.GetRoot());
            }
            catch (Exception exception)
            {
                _receipts.Writer.TryComplete(exception);
                throw;
            }
        });

    internal void Add(TxReceipt receipt)
    {
        if (!_receipts.Writer.TryWrite(receipt)) _receipts.Writer.WriteAsync(receipt).AsTask().GetAwaiter().GetResult();
    }

    internal Task<(Bloom BlockBloom, Hash256 ReceiptsRoot)> Complete()
    {
        _receipts.Writer.TryComplete();
        return _completion;
    }

    public void Dispose()
    {
        _receipts.Writer.TryComplete();
        // A failed/aborted block may have produced fewer receipts. Join without replacing its validation failure.
        try { _completion.GetAwaiter().GetResult(); }
        catch (Exception) when (_completion.IsFaulted) { _ = _completion.Exception; }
    }

    private readonly struct BloomTimeSink : IMetricSink
    {
        public static void AddTicks(long ticks) => Evm.Metrics.IncrementBloomsTime(ticks);
        public static bool IsEnabled => ExecutionMetricsFlag.IsActive;
    }

    private readonly struct RootTimeSink : IMetricSink
    {
        public static void AddTicks(long ticks) => Evm.Metrics.IncrementReceiptsRootTime(ticks);
        public static bool IsEnabled => ExecutionMetricsFlag.IsActive;
    }

    internal sealed class Tracer : BlockReceiptsTracer
    {
        internal StreamingReceiptProcessor? Processor { get; set; }

        protected override TxReceipt BuildReceipt(Address recipient, in GasConsumed gasConsumed, byte statusCode, LogEntry[] logEntries, Hash256? stateRoot)
        {
            TxReceipt receipt = base.BuildReceipt(recipient, gasConsumed, statusCode, logEntries, stateRoot);
            Processor!.Add(receipt);
            return receipt;
        }
    }
}
