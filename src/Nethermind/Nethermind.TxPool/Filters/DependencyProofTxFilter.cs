// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.TxPool.Filters;

/// <summary>Requires verified EIP-8288 witnesses before rejected hashes are cached.</summary>
internal sealed class DependencyProofTxFilter(LeanProofStore? proofStore) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (state.HeadSpec.IsEip8288Enabled && Eip8288Dependencies.RecursiveStarkGas(tx) != 0)
        {
            if (!FrameTxValidation.IsWellFormed(tx, state.HeadSpec, out string? error))
                return AcceptTxResult.Invalid.WithMessage(error!);
            List<FrameDependency> dependencies = Eip8288Dependencies.ForTransaction(tx);
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
