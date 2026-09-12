// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Metric;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State.Proofs;

namespace Nethermind.Consensus.Processing;

public partial class BlockProcessor
{
    private partial ReceiptCommitmentStream? CreateReceiptStream(Block block, IBlockTracer tracer,
        ProcessingOptions options, IReleaseSpec spec, CancellationToken token, out BlockValidationTransactionsExecutor? executor);

    internal sealed class ReceiptCommitmentStream : IDisposable
    {
        private readonly Channel<TxReceipt> _receipts;
        private readonly CancellationTokenSource _cancellation;
        private readonly ReceiptTrie.StreamingRoot _root;
        private readonly int _receiptCount;
        private readonly ILogger _logger;
        private int _published;

        public ReceiptCommitmentStream(ReceiptTrie.StreamingRoot root,
            int receiptCount, CancellationToken token, ILogger logger)
        {
            _root = root;
            _receiptCount = receiptCount;
            _logger = logger;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            _receipts = Channel.CreateUnbounded<TxReceipt>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });
            Result = Task.Run(ConsumeAsync);
        }

        public Task<(Bloom BlockBloom, Hash256 ReceiptsRoot)> Result { get; }

        public void Add(TxReceipt receipt)
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            if (Result.IsFaulted) Result.GetAwaiter().GetResult();
            if (_published == _receiptCount) throw new InvalidOperationException("Too many receipts for the block.");
            // Only the standard sequential executor publishes, after all synchronous receipt observers return.
            if (!_receipts.Writer.TryWrite(receipt)) throw new InvalidOperationException("Receipt stream is closed.");
            _published++;
        }

        public void CompleteAdding() => _receipts.Writer.TryComplete();

        private async Task<(Bloom, Hash256)> ConsumeAsync()
        {
            using (_root)
            {
                Bloom bloom = new();
                await foreach (TxReceipt receipt in _receipts.Reader.ReadAllAsync(_cancellation.Token))
                {
                    _cancellation.Token.ThrowIfCancellationRequested();
                    using (MetricsTimer<BloomsTimeSink> _ = new())
                    {
                        receipt.CalculateBloom();
                        bloom.Accumulate(receipt.Bloom);
                    }
                    using (MetricsTimer<ReceiptsRootTimeSink> _ = new())
                    {
                        _root.Add(receipt);
                    }
                }

                _cancellation.Token.ThrowIfCancellationRequested();
                using (MetricsTimer<ReceiptsRootTimeSink> _ = new())
                {
                    return (bloom, _root.Complete());
                }
            }
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            _receipts.Writer.TryComplete();
            try
            {
                Result.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception) when (Result.IsFaulted)
            {
                _logger.DebugError("Receipt commitment worker failed.", exception);
            }
            finally
            {
                _cancellation.Dispose();
            }
        }
    }
}
