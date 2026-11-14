// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.TxPool.Filters;

/// <summary>Requires verified EIP-8288 witnesses before rejected hashes are cached.</summary>
internal sealed class DependencyProofTxFilter(LeanProofStore? proofStore) : IIncomingTxFilter
{
    /// <summary>ethereum/EIPs#12664: batch claim and leaf payment transactions go to builders directly, never through the public
    /// mempool.</summary>
    internal const string BatchedDependencyNotPooled = "Batch claim and leaf payment transactions are submitted to builders, not the public mempool.";

    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (state.HeadSpec.IsEip8288Enabled && Eip8288Dependencies.IsDependencyBearing(tx))
        {
            if (!FrameTxValidation.IsWellFormed(tx, state.HeadSpec, out string? error))
                return AcceptTxResult.Invalid.WithMessage(error!);
            if (Eip8288Dependencies.DeclaresBatched(tx))
                return AcceptTxResult.Invalid.WithMessage(BatchedDependencyNotPooled);
            List<FrameDependency> dependencies = Eip8288Dependencies.ForTransaction(tx, FrameTxSigHash.ComputeValue);
            state.ProofDependencies = dependencies;
            (int sphincs, int stark) = Eip8288Dependencies.CountByScheme(dependencies);
            if (sphincs > Eip8288Constants.MaxSigsPerTx || stark > Eip8288Constants.MaxStarksPerTx)
                return AcceptTxResult.TooManyDependencies;
            if (proofStore is null || !proofStore.TryReserveTransaction(dependencies, out state.ProofReservation))
                return AcceptTxResult.MissingDependencyProof;
        }
        return AcceptTxResult.Accepted;
    }
}

/// <summary>Accounts for witnesses against the recovered sender before pool insertion.</summary>
internal sealed class DependencyProofSenderQuotaTxFilter(LeanProofStore? proofStore) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (state.ProofDependencies is { Count: > 0 } dependencies
            && (tx.SenderAddress is null || proofStore is null
                || !proofStore.TryReserveTransaction(dependencies, out state.SenderProofReservation, tx.SenderAddress)))
            return AcceptTxResult.MissingDependencyProof.WithMessage("Proof witness capacity is full for this sender; retry later.");
        return AcceptTxResult.Accepted;
    }
}
