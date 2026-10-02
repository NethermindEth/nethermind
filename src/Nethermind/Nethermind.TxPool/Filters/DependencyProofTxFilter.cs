// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            (int sphincs, int stark) = Eip8288Dependencies.CountByScheme(Eip8288Dependencies.ForTransaction(tx));
            if (sphincs > Eip8288Constants.MaxSigsPerTx || stark > Eip8288Constants.MaxStarksPerTx)
                return AcceptTxResult.TooManyDependencies;
            if (proofStore is null || !proofStore.Covers(tx))
                return AcceptTxResult.MissingDependencyProof;
        }
        return AcceptTxResult.Accepted;
    }
}
