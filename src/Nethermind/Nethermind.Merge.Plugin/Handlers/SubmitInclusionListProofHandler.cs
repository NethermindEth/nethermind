// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.ProofAggregation;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>Parameters of <c>engine_submitInclusionListProofV1(timestamp, il_proof)</c>.</summary>
public sealed record SubmitInclusionListProofRequest(ulong Timestamp, InclusionListProofV1? IlProof);

/// <summary>
/// EIP-8288 <c>engine_submitInclusionListProofV1</c>: verifies the <c>il_proof</c> of a <c>SignedInclusionListProof</c> from
/// the proof topic under the fork at <c>timestamp</c>, so a later list committing to it is checked by cache lookup (EIPs#12475).
/// </summary>
/// <remarks>A proof over the size bounds, or whose <c>proof_deps</c> are not sorted, deduplicated and within
/// <c>MAX_DEPS_PER_AGGREGATE_WRAPPER</c>, is <c>INVALID</c> before verification. A valid proof is retained until the view freeze
/// deadline of its list slot, and through the retrieval window once a list's commitment to it is answered valid.</remarks>
public sealed class SubmitInclusionListProofHandler(ISpecProvider specProvider, InclusionListProofService inclusionListProofs)
    : IHandler<SubmitInclusionListProofRequest, InclusionListProofStatusV1>
{
    public ResultWrapper<InclusionListProofStatusV1> Handle(SubmitInclusionListProofRequest request)
    {
        if (!specProvider.GetSpec(ForkActivation.TimestampOnly(request.Timestamp)).IsEip8288Enabled)
            return ResultWrapper<InclusionListProofStatusV1>.Fail(MergeErrorMessages.UnsupportedFork, MergeErrorCodes.UnsupportedFork);
        if (request.IlProof is not { } encoded) return ResultWrapper<InclusionListProofStatusV1>.Fail("ilProof must be set", ErrorCodes.InvalidParams);
        if (encoded.TryDecode(out InclusionListProof? ilProof) is { } error)
            return ResultWrapper<InclusionListProofStatusV1>.Success(InclusionListProofStatusV1.Invalid(error));
        InclusionListProofStatus status = inclusionListProofs.Submit(request.Timestamp, ilProof!, out string? reason);
        return ResultWrapper<InclusionListProofStatusV1>.Success(InclusionListProofStatusV1.From(status, reason));
    }
}
