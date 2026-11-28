// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>The identifier of an EIP-7805 inclusion check (bogota.md § Deferred inclusion checking).</summary>
/// <remarks>
/// <c>keccak256(b"engine/inclusion-check/v1" || RLP([blockHash, inclusionLists, claims]))</c>, where <c>claims</c> is
/// <c>[[transactionHash, evaluationIndex], ...]</c>. List boundaries, committee positions, transaction order,
/// duplicates and claim order all count, so different inputs for one payload name different checks. Local API state,
/// not a consensus commitment.
/// </remarks>
public static class InclusionCheckId
{
    private static readonly byte[] Domain = "engine/inclusion-check/v1"u8.ToArray();

    public static Hash256 Compute(Hash256 blockHash, byte[][][] inclusionLists, OmissionClaim[] claims)
    {
        byte[] encoded = RlpCodec.Write((blockHash, inclusionLists, claims), static (ref RlpWriter writer, (Hash256 BlockHash, byte[][][] InclusionLists, OmissionClaim[] Claims) input) =>
            writer.WriteSequence(input, static (ref RlpWriter writer, (Hash256 BlockHash, byte[][][] InclusionLists, OmissionClaim[] Claims) input) =>
            {
                writer.Encode(input.BlockHash);
                writer.WriteSequence(input.InclusionLists, WriteInclusionLists);
                writer.WriteSequence(input.Claims, WriteClaims);
            }));

        return Keccak.Compute([.. Domain, .. encoded]);
    }

    private static void WriteInclusionLists(ref RlpWriter writer, byte[][][] inclusionLists)
    {
        foreach (byte[][] inclusionList in inclusionLists)
        {
            writer.WriteSequence(inclusionList, static (ref RlpWriter writer, byte[][] transactions) =>
            {
                foreach (byte[] transaction in transactions) writer.Write(transaction);
            });
        }
    }

    private static void WriteClaims(ref RlpWriter writer, OmissionClaim[] claims)
    {
        foreach (OmissionClaim claim in claims)
        {
            writer.WriteSequence(claim, static (ref RlpWriter writer, OmissionClaim claim) =>
            {
                writer.Encode(claim.TransactionHash);
                writer.Write(claim.EvaluationIndex);
            });
        }
    }
}
