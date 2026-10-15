// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// EIP-8288 <c>engine_getInclusionListProofV1(stark_proof_hash)</c>: the <c>il_proof = [stark_proof, proof_deps]</c> this node holds
/// under that hash, or null (EIPs#12475).
/// </summary>
/// <remarks>The proof comes from a validated mode-1 wrapper, whose <c>deps</c> are its <c>proof_deps</c>, or from a verified
/// <c>il_proof</c>; both are held only after verification. A committee member serves it so the consensus layer can gossip it,
/// and attesters lacking the proof fetch it. Gated, like <c>engine_getInclusionListV1</c>, on the final spec.</remarks>
public sealed class GetInclusionListProofHandler(ISpecProvider specProvider, LeanProofStore proofStore) : IHandler<Hash256, InclusionListProofV1?>
{
    public ResultWrapper<InclusionListProofV1?> Handle(Hash256 starkProofHash)
    {
        IReleaseSpec spec = specProvider.GetFinalSpec();
        if (!spec.IsEip7805Enabled || !spec.IsEip8288Enabled)
            return ResultWrapper<InclusionListProofV1?>.Fail(MergeErrorMessages.UnsupportedFork, MergeErrorCodes.UnsupportedFork);
        if (!proofStore.TryGetRecursiveProof(starkProofHash.ValueHash256, out byte[]? proof, out FrameDependency[]? dependencies))
            return ResultWrapper<InclusionListProofV1?>.Success(null);
        byte[][] proofDeps = new byte[dependencies!.Length][];
        for (int i = 0; i < proofDeps.Length; i++) proofDeps[i] = Eip8288Dependencies.Serialize([dependencies[i]]);
        return ResultWrapper<InclusionListProofV1?>.Success(new InclusionListProofV1 { StarkProof = proof, ProofDeps = proofDeps });
    }
}
