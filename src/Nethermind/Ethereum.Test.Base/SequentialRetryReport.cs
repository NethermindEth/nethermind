// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;

namespace Ethereum.Test.Base;

/// <summary>
/// Reports a block the parallel executor rejected and <see cref="BranchProcessor"/> re-ran sequentially as one
/// retry carrying both errors, instead of as a second decision.
/// </summary>
/// <remarks>
/// <see cref="BranchProcessor"/> retries a block by calling <see cref="IBlockProcessor.ProcessOne"/> again for it with
/// <see cref="ProcessingOptions.ForceSequentialBlockAccessList"/> right after the parallel attempt threw, so the
/// processor returned by <see cref="Decorate"/> sees both calls. Built per test.
/// </remarks>
public sealed class SequentialRetryReport(IBlockAccessListExecutionReport report) : IBlockAccessListExecutionObserver
{
    private readonly IBlockAccessListExecutionReport _report = report;
    private Block? _retrying;

    public void OnExecutionPathChosen(Block block, string? sequentialReason)
    {
        if (!ReferenceEquals(block, _retrying)) _report.OnExecutionPathChosen(block, sequentialReason);
    }

    public IBlockProcessor Decorate(IBlockProcessor processor) => new RetryWatchingBlockProcessor(processor, this);

    private sealed class RetryWatchingBlockProcessor(IBlockProcessor inner, SequentialRetryReport owner) : IBlockProcessor
    {
        private (Block Block, Exception Error)? _parallelFailure;

        public event Action<Block>? TransactionsExecuted
        {
            add => inner.TransactionsExecuted += value;
            remove => inner.TransactionsExecuted -= value;
        }

        public (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token = default)
        {
            (Block Block, Exception Error)? lastFailure = _parallelFailure;
            _parallelFailure = null;
            if (!options.ContainsFlag(ProcessingOptions.ForceSequentialBlockAccessList))
            {
                try
                {
                    return inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
                }
                catch (Exception error)
                {
                    // The node wraps the parallel executor's error; the retry, if any, follows this call.
                    _parallelFailure = (suggestedBlock, error.InnerException ?? error);
                    throw;
                }
            }

            if (lastFailure is not { } failure || !ReferenceEquals(failure.Block, suggestedBlock))
            {
                return inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
            }

            owner._retrying = suggestedBlock;
            try
            {
                (Block Block, TxReceipt[] Receipts) result = inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
                owner._report.OnSequentialRetry(suggestedBlock, failure.Error, null);
                return result;
            }
            catch (Exception sequentialError)
            {
                owner._report.OnSequentialRetry(suggestedBlock, failure.Error, sequentialError);
                throw;
            }
            finally
            {
                owner._retrying = null;
            }
        }
    }
}
