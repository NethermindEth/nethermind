// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>Parameters of <c>engine_verifyInclusionListProofV1(transactions, deps_hash, stark_proof)</c>.</summary>
/// <param name="Transactions">The signed list's EIP-2718 transaction encodings, in order.</param>
/// <param name="DepsHash">The <c>deps_hash</c> of the list's signed <c>recursive_stark</c>.</param>
/// <param name="StarkProof">The received proof, which the consensus layer has checked against <c>stark_proof_hash</c>.</param>
public sealed record VerifyInclusionListProofRequest(byte[][]? Transactions, Hash256? DepsHash, byte[]? StarkProof);

/// <summary>
/// EIP-8288 <c>engine_verifyInclusionListProofV1</c>: the consensus layer holds a committed list's proof bytes and the
/// execution layer the verifier, so attesters verify a received proof here before counting the list (EIPs#12475).
/// </summary>
/// <remarks>
/// The list's <c>deps</c> is the union over the entries that decode and pass EIP-8141 and EIP-8288 static validity; others
/// contribute none. The proof is valid only if <c>deps</c> is nonempty, <c>deps_hash == get_deps_hash(deps)</c> and the proof
/// verifies against it and <c>AGGREGATED_VK</c>. Verdicts are cached by <c>(keccak256(proof), deps_hash)</c>. Gated, like
/// <c>engine_getInclusionListV1</c>, on the final spec, whose rules also define static validity.
/// </remarks>
public sealed class VerifyInclusionListProofHandler(ISpecProvider specProvider, ILeanProofVerifier proofVerifier)
    : IHandler<VerifyInclusionListProofRequest, InclusionListProofStatusV1>
{
    public ResultWrapper<InclusionListProofStatusV1> Handle(VerifyInclusionListProofRequest request)
    {
        IReleaseSpec spec = specProvider.GetFinalSpec();
        if (!spec.IsEip7805Enabled || !spec.IsEip8288Enabled)
            return ResultWrapper<InclusionListProofStatusV1>.Fail(MergeErrorMessages.UnsupportedFork, MergeErrorCodes.UnsupportedFork);
        if (Validate(request) is { } error) return ResultWrapper<InclusionListProofStatusV1>.Fail(error, ErrorCodes.InvalidParams);

        // Undecodable entries contribute no dependencies, as in the counted list.
        Transaction[] transactions = TxsDecoder.DecodeTxs(request.Transactions!, skipErrors: true).Transactions;
        return ResultWrapper<InclusionListProofStatusV1>.Success(
            InclusionListProofValidator.VerifyCommitment(transactions, request.DepsHash!.ValueHash256, request.StarkProof!, proofVerifier, spec,
                out string? invalid)
                ? InclusionListProofStatusV1.Valid
                : InclusionListProofStatusV1.Invalid(invalid!));
    }

    private static string? Validate(VerifyInclusionListProofRequest request)
    {
        if (request.Transactions is not { } transactions) return "Inclusion list transactions must be set";
        if (request.DepsHash is null) return "deps_hash must be set";
        // A null commitment has no proof to verify; the consensus layer counts such a list without this call.
        if (request.StarkProof is null) return "stark_proof must be set";
        if (transactions.Length > Eip7805Constants.MaxTransactionsPerInclusionList) return "Inclusion list exceeds the maximum number of transactions";
        long bytes = 0;
        foreach (byte[]? transaction in transactions)
        {
            if (transaction is null) return "Inclusion list transactions must be set";
            bytes += transaction.Length;
        }
        return bytes > Eip7805Constants.MaxBytesPerInclusionList ? "Inclusion list exceeds MAX_TRANSACTIONS_BYTES_PER_INCLUSION_LIST" : null;
    }
}
