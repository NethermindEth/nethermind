// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.ProofAggregation;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>Parameters of <c>engine_verifyInclusionListProofV1(timestamp, transactions, recursiveStark, ilProof, attestationDeadlineMs)</c>.</summary>
/// <param name="Timestamp">The target block's timestamp, whose fork fixes static validity and <c>AGGREGATED_VK</c>.</param>
/// <param name="Transactions">The signed list's EIP-2718 transaction encodings, in order.</param>
/// <param name="RecursiveStark">The list's non-null <c>recursive_stark</c>.</param>
/// <param name="IlProof">The <c>il_proof</c> fetched by <c>stark_proof_hash</c>, or null.</param>
/// <param name="AttestationDeadlineMs">The EIP-7805 attestation deadline of the target block's slot, in Unix milliseconds.</param>
public sealed record VerifyInclusionListProofRequest(ulong Timestamp, byte[][]? Transactions, RecursiveStarkV1? RecursiveStark, InclusionListProofV1? IlProof,
    ulong AttestationDeadlineMs);

/// <summary>
/// EIP-8288 <c>engine_verifyInclusionListProofV1</c>: whether a counted list's commitment is valid, so the consensus layer
/// passes it to block building and <c>engine_newPayload</c> (EIPs#12475, execution-apis#945).
/// </summary>
/// <remarks>A commitment to a proof whose bytes this node holds, usually a validated mode-1 wrapper's or a submitted topic
/// proof's, is a cache lookup with a subset test, and <c>VALID</c> retains the bytes until the attestation deadline. Otherwise
/// the answer is <c>PROOF_UNKNOWN</c> until the consensus layer supplies the matching <c>il_proof</c>. An <c>il_proof</c> over
/// its bounds is no invalid parameter: <c>INVALID</c> if it matches the commitment and ignored otherwise.</remarks>
public sealed class VerifyInclusionListProofHandler(ISpecProvider specProvider, InclusionListProofService inclusionListProofs)
    : IHandler<VerifyInclusionListProofRequest, InclusionListProofStatusV1>
{
    public ResultWrapper<InclusionListProofStatusV1> Handle(VerifyInclusionListProofRequest request)
    {
        if (!specProvider.GetSpec(ForkActivation.TimestampOnly(request.Timestamp)).IsEip8288Enabled)
            return ResultWrapper<InclusionListProofStatusV1>.Fail(MergeErrorMessages.UnsupportedFork, MergeErrorCodes.UnsupportedFork);
        if (Validate(request, out InclusionListProof? ilProof) is { } error)
            return ResultWrapper<InclusionListProofStatusV1>.Fail(error, ErrorCodes.InvalidParams);

        // Undecodable entries contribute no dependencies, as in the counted list.
        Transaction[] transactions = TxsDecoder.DecodeTxs(request.Transactions!, skipErrors: true).Transactions;
        InclusionListCommitment commitment = request.RecursiveStark!.ToCommitment();
        InclusionListProofStatus status = inclusionListProofs.Verify(request.Timestamp, transactions, in commitment, ilProof,
            request.AttestationDeadlineMs, out string? reason);
        return ResultWrapper<InclusionListProofStatusV1>.Success(InclusionListProofStatusV1.From(status, reason));
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
        return request.IlProof is { } proof ? proof.TryDecode(out ilProof) : null;
    }
}
