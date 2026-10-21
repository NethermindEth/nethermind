// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.ProofAggregation;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>Parameters of <c>engine_submitInclusionListProofV1(timestamp, ilProof, viewFreezeDeadlineMs)</c>.</summary>
/// <param name="Timestamp">The timestamp of the block constrained by the inclusion lists of the proof's slot.</param>
/// <param name="IlProof">The <c>il_proof</c> of a <c>SignedInclusionListProof</c> from the proof topic.</param>
/// <param name="ViewFreezeDeadlineMs">The EIP-7805 view freeze deadline of the proof's slot, in Unix milliseconds.</param>
public sealed record SubmitInclusionListProofRequest(ulong Timestamp, InclusionListProofV1? IlProof, ulong ViewFreezeDeadlineMs);

/// <summary>
/// EIP-8288 <c>engine_submitInclusionListProofV1</c>: verifies the <c>il_proof</c> of a <c>SignedInclusionListProof</c> from
/// the proof topic under the fork at <c>timestamp</c> and records its verdict, so a later list committing to it is checked by
/// cache lookup (EIPs#12475, execution-apis#945).
/// </summary>
/// <remarks>A proof over the size bounds, or whose <c>proof_deps</c> are not sorted, deduplicated, within
/// <c>MAX_DEPS_PER_AGGREGATE_WRAPPER</c> and of enabled schemes, is <c>INVALID</c> without verification; only a
/// <c>proofDeps</c> entry that is not 96 bytes is an invalid parameter. A valid proof's bytes are retained until
/// <c>viewFreezeDeadlineMs</c>, and until the attestation deadline once a list's commitment to it is answered valid.</remarks>
public sealed class SubmitInclusionListProofHandler(ISpecProvider specProvider, InclusionListProofService inclusionListProofs)
    : IHandler<SubmitInclusionListProofRequest, InclusionListProofStatusV1>
{
    public ResultWrapper<InclusionListProofStatusV1> Handle(SubmitInclusionListProofRequest request)
    {
        if (!specProvider.GetSpec(ForkActivation.TimestampOnly(request.Timestamp)).IsEip8288Enabled)
            return ResultWrapper<InclusionListProofStatusV1>.Fail(MergeErrorMessages.UnsupportedFork, MergeErrorCodes.UnsupportedFork);
        if (request.IlProof is not { } encoded) return ResultWrapper<InclusionListProofStatusV1>.Fail("ilProof must be set", ErrorCodes.InvalidParams);
        if (encoded.TryDecode(out InclusionListProof? ilProof) is { } error)
            return ResultWrapper<InclusionListProofStatusV1>.Fail(error, ErrorCodes.InvalidParams);
        InclusionListProofStatus status = inclusionListProofs.Submit(request.Timestamp, ilProof!, request.ViewFreezeDeadlineMs, out string? reason);
        return ResultWrapper<InclusionListProofStatusV1>.Success(InclusionListProofStatusV1.From(status, reason));
    }
}
