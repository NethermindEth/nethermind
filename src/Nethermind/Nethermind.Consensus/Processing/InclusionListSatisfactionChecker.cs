// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Processing;

/// <inheritdoc cref="IInclusionListSatisfactionChecker"/>
public sealed class InclusionListSatisfactionChecker(ISpecProvider specProvider, ITxValidator txValidator,
    ITransactionProcessor? transactionProcessor = null, ILeanProofVerifier? proofVerifier = null)
    : IInclusionListSatisfactionChecker
{
    public bool IsSatisfied(Block processedBlock, Block suggestedBlock, IWorldState worldState)
    {
        IReleaseSpec spec = specProvider.GetSpec(processedBlock.Header);
        return InclusionListValidator.IsSatisfied(processedBlock, suggestedBlock.InclusionListTransactions,
            worldState, spec, txValidator, suggestedBlock.InclusionListRecursiveStark,
            proofVerifier ?? NativeLeanProofVerifier.Instance, CouldAppendFrame);

        bool CouldAppendFrame(Transaction tx)
        {
            ITransactionProcessor processor = transactionProcessor
                ?? throw new InvalidOperationException("Frame inclusion lists require a transaction processor.");
            IBlockAccessListSource? recorder = worldState as IBlockAccessListSource;
            BlockAccessListAtIndex? recordedAccesses = recorder?.GeneratedBlockAccessList;
            recorder?.SetGeneratingBlockAccessList(null);
            Snapshot snapshot = worldState.TakeSnapshot();
            try
            {
                // Transaction appendability precedes withdrawals, even though this state includes them.
                foreach (Withdrawal withdrawal in processedBlock.Withdrawals ?? [])
                    worldState.SubtractFromBalance(withdrawal.Address, withdrawal.AmountInWei, spec, out _);
                Address sender = tx.SenderAddress!;
                if (tx.NonceKeys is { } keys
                    ? !KeyedNonceManager.IsNonceSetValid(worldState, sender, keys, tx.Nonce)
                    : worldState.GetNonce(sender) != tx.Nonce) return false;

                FrameTxValidationTracer tracer = new(sender, Eip8141Constants.ExpiryVerifierAddress, worldState, spec);
                processor.SetBlockExecutionContext(processedBlock.Header.Clone());
                try
                {
                    return processor.Process(tx, tracer, ExecutionOptions.FrameValidationPrefixOnly | ExecutionOptions.FramePrefixAtExecutionBlock)
                        && !tracer.Violated && tracer.Payer is not null;
                }
                catch (OperationCanceledException) when (tracer.Violated)
                {
                    return false;
                }
            }
            finally
            {
                worldState.Restore(snapshot);
                recorder?.SetGeneratingBlockAccessList(recordedAccesses);
                processor.SetBlockExecutionContext(processedBlock.Header);
            }
        }
    }
}
