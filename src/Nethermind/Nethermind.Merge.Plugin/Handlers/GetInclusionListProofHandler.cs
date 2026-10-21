// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.ProofAggregation;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>Parameters of <c>engine_getInclusionListProofV1(timestamp, stark_proof_hash)</c>.</summary>
public sealed record GetInclusionListProofRequest(ulong Timestamp, Hash256? StarkProofHash);

/// <summary>
/// EIP-8288 <c>engine_getInclusionListProofV1</c>: the <c>il_proof = [stark_proof, proof_deps]</c> whose bytes this node holds
/// under <c>stark_proof_hash</c> with a <c>VALID</c> verdict under the fork at <c>timestamp</c>, or null (EIPs#12475,
/// execution-apis#945).
/// </summary>
/// <remarks>The proof comes from a fully validated mode-1 wrapper, whose <c>deps</c> are its <c>proof_deps</c>, or from a held
/// <c>il_proof</c>; a verdict whose bytes were released yields null. The consensus layer answers its peers' requests for
/// committed proofs from here.</remarks>
public sealed class GetInclusionListProofHandler(ISpecProvider specProvider, InclusionListProofService inclusionListProofs)
    : IHandler<GetInclusionListProofRequest, InclusionListProofV1?>
{
    public ResultWrapper<InclusionListProofV1?> Handle(GetInclusionListProofRequest request)
    {
        if (!specProvider.GetSpec(ForkActivation.TimestampOnly(request.Timestamp)).IsEip8288Enabled)
            return ResultWrapper<InclusionListProofV1?>.Fail(MergeErrorMessages.UnsupportedFork, MergeErrorCodes.UnsupportedFork);
        if (request.StarkProofHash is not { } starkProofHash)
            return ResultWrapper<InclusionListProofV1?>.Fail("starkProofHash must be set", ErrorCodes.InvalidParams);
        InclusionListProof? proof = inclusionListProofs.Get(request.Timestamp, starkProofHash.ValueHash256);
        return ResultWrapper<InclusionListProofV1?>.Success(proof is null ? null : InclusionListProofV1.From(proof));
    }
}
