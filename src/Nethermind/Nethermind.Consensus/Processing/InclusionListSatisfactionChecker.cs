// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Processing;

/// <inheritdoc cref="IInclusionListSatisfactionChecker"/>
public sealed class InclusionListSatisfactionChecker(ISpecProvider specProvider, ITxValidator txValidator,
    ITransactionProcessor? transactionProcessor = null)
    : IInclusionListSatisfactionChecker
{
    public bool IsSatisfied(Block processedBlock, Block suggestedBlock, IReadOnlyStateProvider worldState)
    {
        IReleaseSpec spec = specProvider.GetSpec(processedBlock.Header);
        return InclusionListValidator.IsSatisfied(processedBlock, suggestedBlock.InclusionListTransactions,
            worldState, spec, txValidator, suggestedBlock.InclusionLists, CouldAppendFrame);

        bool CouldAppendFrame(Transaction tx)
        {
            ITransactionProcessor processor = transactionProcessor
                ?? throw new InvalidOperationException("Frame inclusion lists require a transaction processor.");
            IWorldState mutableState = worldState as IWorldState
                ?? throw new InvalidOperationException("Frame inclusion lists require transaction-boundary execution state.");
            IBlockAccessListSource? recorder = mutableState as IBlockAccessListSource;
            BlockAccessListAtIndex? recordedAccesses = recorder?.GeneratedBlockAccessList;
            recorder?.SetGeneratingBlockAccessList(null);
            Snapshot snapshot = mutableState.TakeSnapshot();
            try
            {
                Address sender = tx.SenderAddress!;
                if (tx.NonceKeys is { } keys
                    ? !KeyedNonceManager.IsNonceSetValid(mutableState, sender, keys, tx.Nonce)
                    : worldState.GetNonce(sender) != tx.Nonce) return false;

                FrameTxValidationTracer tracer = new(sender, Eip8141Constants.ExpiryVerifierAddress, mutableState, spec);
                processor.SetBlockExecutionContext(processedBlock.Header.Clone());
                try
                {
                    return processor.Process(tx, tracer, ExecutionOptions.FrameValidationPrefixOnly)
                        && !tracer.Violated && tracer.Payer is not null;
                }
                catch (OperationCanceledException) when (tracer.Violated)
                {
                    return false;
                }
            }
            finally
            {
                mutableState.Restore(snapshot);
                recorder?.SetGeneratingBlockAccessList(recordedAccesses);
                processor.SetBlockExecutionContext(processedBlock.Header);
            }
        }
    }
}
