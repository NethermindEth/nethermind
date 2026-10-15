// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>Parameters of <c>engine_verifyInclusionListProofV1(transactions, recursive_stark, il_proof)</c>.</summary>
/// <param name="Transactions">The signed list's EIP-2718 transaction encodings, in order.</param>
/// <param name="RecursiveStark">The list's non-null <c>recursive_stark</c>.</param>
/// <param name="IlProof">The <c>il_proof</c> fetched by <c>stark_proof_hash</c>, or null.</param>
public sealed record VerifyInclusionListProofRequest(byte[][]? Transactions, RecursiveStarkV1? RecursiveStark, InclusionListProofV1? IlProof);

/// <summary>
/// EIP-8288 <c>engine_verifyInclusionListProofV1</c>: attesters check a committed list here before counting it (EIPs#12475).
/// </summary>
/// <remarks>
/// A commitment to a proof this node already holds, usually a validated mode-1 wrapper's, is answered from the verdict cache
/// with a subset test and no verification. Otherwise the answer is <c>PROOF_UNKNOWN</c> until the consensus layer supplies the
/// matching <c>il_proof</c>, which is verified and retained for building. Gated, like <c>engine_getInclusionListV1</c>, on the
/// final spec, whose rules also define static validity.
/// </remarks>
public sealed class VerifyInclusionListProofHandler(ISpecProvider specProvider, ILeanProofVerifier proofVerifier, LeanProofStore? proofStore = null)
    : IHandler<VerifyInclusionListProofRequest, InclusionListProofStatusV1>
{
    public ResultWrapper<InclusionListProofStatusV1> Handle(VerifyInclusionListProofRequest request)
    {
        IReleaseSpec spec = specProvider.GetFinalSpec();
        if (!spec.IsEip7805Enabled || !spec.IsEip8288Enabled)
            return ResultWrapper<InclusionListProofStatusV1>.Fail(MergeErrorMessages.UnsupportedFork, MergeErrorCodes.UnsupportedFork);
        if (Validate(request, out InclusionListProof? ilProof) is { } error)
            return ResultWrapper<InclusionListProofStatusV1>.Fail(error, ErrorCodes.InvalidParams);

        // Undecodable entries contribute no dependencies, as in the counted list.
        Transaction[] transactions = TxsDecoder.DecodeTxs(request.Transactions!, skipErrors: true).Transactions;
        InclusionListCommitment commitment = request.RecursiveStark!.ToCommitment();
        InclusionListProofStatus status = InclusionListProofValidator.VerifyCommitment(transactions, in commitment, ilProof, proofVerifier, spec,
            out string? reason, proofStore);
        return ResultWrapper<InclusionListProofStatusV1>.Success(status switch
        {
            InclusionListProofStatus.Valid => InclusionListProofStatusV1.Valid,
            InclusionListProofStatus.Invalid => InclusionListProofStatusV1.Invalid(reason!),
            _ => InclusionListProofStatusV1.ProofUnknown(reason)
        });
    }

    private static string? Validate(VerifyInclusionListProofRequest request, out InclusionListProof? ilProof)
    {
        ilProof = null;
        if (request.Transactions is not { } transactions) return "Inclusion list transactions must be set";
        // A null commitment needs no check: the consensus layer counts such a list without this call.
        if (request.RecursiveStark is not { IsComplete: true }) return "recursiveStark must set starkProofHash and depsHash";
        if (transactions.Length > Eip7805Constants.MaxTransactionsPerInclusionList) return "Inclusion list exceeds the maximum number of transactions";
        long bytes = 0;
        foreach (byte[]? transaction in transactions)
        {
            if (transaction is null) return "Inclusion list transactions must be set";
            bytes += transaction.Length;
        }
        if (bytes > Eip7805Constants.MaxBytesPerInclusionList) return "Inclusion list exceeds MAX_TRANSACTIONS_BYTES_PER_INCLUSION_LIST";
        if (request.IlProof is not { } proof) return null;
        if (proof.StarkProof is null || proof.ProofDeps is not { } encoded) return "ilProof must set starkProof and proofDeps";
        // Interfaces bound il_proof data; the proof size and dependency order are checked against the commitment.
        if (proof.StarkProof.Length > Eip8288Constants.MaxAggregateProofBytes) return "ilProof starkProof exceeds MAX_AGGREGATE_PROOF_BYTES";
        if (encoded.Length > Eip8288Constants.MaxDepsPerAggregate) return "ilProof proofDeps exceed MAX_DEPS_PER_AGGREGATE_WRAPPER";
        List<FrameDependency> proofDeps = new(encoded.Length);
        foreach (byte[]? dependency in encoded)
        {
            if (dependency is not { Length: Eip8288Constants.DependencyTripleLength } || !dependency.AsSpan(0, 31).IsZero())
                return "ilProof proofDeps entries must be 96-byte dependency encodings";
            proofDeps.AddRange(Eip8288Dependencies.Parse(dependency));
        }
        ilProof = new InclusionListProof(proof.StarkProof, proofDeps);
        return null;
    }
}
