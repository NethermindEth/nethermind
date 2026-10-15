// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
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
        int listsLength = 0;
        foreach (byte[][] inclusionList in inclusionLists) listsLength += Rlp.LengthOfSequence(TransactionsLength(inclusionList));

        int claimsLength = 0;
        foreach (OmissionClaim claim in claims) claimsLength += Rlp.LengthOfSequence(ClaimLength(claim));

        KeccakRlpWriter writer = new();
        WriteRaw(ref writer, Domain);
        writer.StartSequence(Rlp.LengthOfKeccakRlp + Rlp.LengthOfSequence(listsLength) + Rlp.LengthOfSequence(claimsLength));
        writer.Encode(blockHash);

        writer.StartSequence(listsLength);
        foreach (byte[][] inclusionList in inclusionLists)
        {
            writer.StartSequence(TransactionsLength(inclusionList));
            foreach (byte[] transaction in inclusionList) writer.Encode(transaction);
        }

        writer.StartSequence(claimsLength);
        foreach (OmissionClaim claim in claims)
        {
            writer.StartSequence(ClaimLength(claim));
            writer.Encode(claim.TransactionHash);
            writer.Encode(claim.EvaluationIndex);
        }

        return writer.GetHash();
    }

    private static void WriteRaw<TWriter>(ref TWriter writer, byte[] bytes) where TWriter : struct, IRlpWriteBackend, allows ref struct
        => writer.Write(bytes);

    private static int TransactionsLength(byte[][] inclusionList)
    {
        int length = 0;
        foreach (byte[] transaction in inclusionList) length += Rlp.LengthOf(transaction);
        return length;
    }

    private static int ClaimLength(OmissionClaim claim) => Rlp.LengthOfKeccakRlp + Rlp.LengthOf(claim.EvaluationIndex);
}
