// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Producers;

/// <summary>Engine API <c>RecursiveStarkV1</c>: an inclusion list's signed EIP-8288 <c>recursive_stark = [stark_proof_hash, deps_hash]</c>.</summary>
public sealed class RecursiveStarkV1
{
    public Hash256? StarkProofHash { get; set; }

    /// <summary><c>get_deps_hash(proof_deps)</c> of the committed proof, whose dependencies contain the list's.</summary>
    public Hash256? DepsHash { get; set; }

    public bool IsComplete => StarkProofHash is not null && DepsHash is not null;

    public InclusionListCommitment ToCommitment() => new(StarkProofHash!.ValueHash256, DepsHash!.ValueHash256);
}

/// <summary>Engine API <c>InclusionListProofV1</c>: an EIP-8288 <c>il_proof = [stark_proof, proof_deps]</c>.</summary>
public sealed class InclusionListProofV1
{
    public byte[]? StarkProof { get; set; }

    /// <summary>The proof's sorted, deduplicated dependencies, each in the 96-byte encoding.</summary>
    public byte[][]? ProofDeps { get; set; }

    /// <summary>Decodes the fields, requiring only that each <c>proofDeps</c> entry is 96 bytes (<c>bytes96</c>).</summary>
    /// <remarks>Size, order and scheme bounds are not schema constraints (execution-apis#945): the methods answer a proof
    /// violating them <c>INVALID</c>, and <c>get_deps_hash</c> is taken over the entries exactly as given.</remarks>
    /// <returns>The schema violation, or <c>null</c>.</returns>
    public string? TryDecode(out InclusionListProof? ilProof)
    {
        ilProof = null;
        if (StarkProof is null || ProofDeps is not { } encoded) return "ilProof must set starkProof and proofDeps";
        // Check every entry before allocating the concatenation, so a malformed list costs no copy.
        foreach (byte[]? entry in encoded)
            if (entry is not { Length: Eip8288Constants.DependencyTripleLength })
                return "ilProof proofDeps entries must be 96-byte dependency encodings";
        byte[] concatenated = new byte[(long)encoded.Length * Eip8288Constants.DependencyTripleLength];
        bool wideScheme = false;
        for (int i = 0; i < encoded.Length; i++)
        {
            byte[] dependency = encoded[i];
            wideScheme |= !dependency.AsSpan(0, 31).IsZero();
            dependency.CopyTo(concatenated, i * Eip8288Constants.DependencyTripleLength);
        }
        ilProof = new InclusionListProof(StarkProof, Eip8288Dependencies.Parse(concatenated), Eip8288Dependencies.ComputeListDepsHash(concatenated), wideScheme);
        return null;
    }

    public static InclusionListProofV1 From(InclusionListProof ilProof)
    {
        byte[][] proofDeps = new byte[ilProof.ProofDeps.Count][];
        for (int i = 0; i < proofDeps.Length; i++) proofDeps[i] = Eip8288Dependencies.Serialize([ilProof.ProofDeps[i]]);
        return new InclusionListProofV1 { StarkProof = ilProof.StarkProof, ProofDeps = proofDeps };
    }
}

/// <summary>
/// Engine API <c>InclusionListV1</c>: one counted EIP-7805 inclusion list as EIP-8288's <c>[transactions, recursive_stark]</c>,
/// with the committee position that authenticated it.
/// </summary>
/// <remarks>
/// Carried by <c>engine_newPayloadV7</c> and <c>PayloadAttributesV6</c> once EIP-8288 and EIP-7805 are both active, in place of
/// the flattened <c>inclusionListTransactions</c>, so the execution layer derives each list's obligations separately
/// (EIPs#12475). The consensus layer passes a list's <c>recursive_stark</c> only once <c>engine_verifyInclusionListProofV1</c>
/// answered valid on this node, and null otherwise.
/// </remarks>
public sealed class InclusionListV1
{
    /// <summary>Position of the inclusion-list committee member whose list this is.</summary>
    public ulong CommitteeIndex { get; set; }

    /// <summary>The list's EIP-2718 transaction encodings, in order.</summary>
    public byte[][]? Transactions { get; set; }

    /// <summary>The list's signed <c>recursive_stark</c>, or <c>null</c> for a null commitment.</summary>
    /// <remarks>A non-null value obligates the list's dependency-bearing transactions when this node holds a valid proof for it;
    /// otherwise, as with <c>null</c>, only its dependency-free transactions bind.</remarks>
    public RecursiveStarkV1? RecursiveStark { get; set; }

    /// <summary>Checks the per-list Engine API bounds: at most one list per committee position, each within
    /// <c>MAX_TRANSACTIONS_BYTES_PER_INCLUSION_LIST</c>, and a commitment with both hashes when set.</summary>
    /// <remarks>
    /// An undecodable entry or a commitment to a proof this node does not hold is left to per-list processing: the entry
    /// contributes nothing and the commitment is treated as null (EIPs#12475), so one committee member cannot fail the request.
    /// </remarks>
    /// <returns>The violated bound, or <c>null</c>.</returns>
    public static string? Validate(InclusionListV1[]? lists)
    {
        if (lists is null) return "Inclusion lists must be set";
        if (lists.Length > Eip7805Constants.InclusionListCommitteeSize) return "Too many inclusion lists";
        ulong? previous = null;
        foreach (InclusionListV1? list in lists)
        {
            if (list?.Transactions is not { } transactions) return "Inclusion list transactions must be set";
            if (list.CommitteeIndex >= Eip7805Constants.InclusionListCommitteeSize) return "Inclusion list committee index out of range";
            if (previous is { } index && list.CommitteeIndex <= index)
                return "Inclusion lists must be in strictly increasing committee-position order";
            previous = list.CommitteeIndex;
            if (transactions.Length > Eip7805Constants.MaxTransactionsPerInclusionList) return "Inclusion list exceeds the maximum number of transactions";
            long bytes = 0;
            foreach (byte[]? transaction in transactions)
            {
                if (transaction is null) return "Inclusion list transactions must be set";
                bytes += transaction.Length;
            }
            if (bytes > Eip7805Constants.MaxBytesPerInclusionList) return "Inclusion list exceeds MAX_TRANSACTIONS_BYTES_PER_INCLUSION_LIST";
            if (list.RecursiveStark is { IsComplete: false }) return "Inclusion-list recursiveStark must set both hashes";
        }
        return null;
    }

    /// <summary>EIP-7805's flattened list: every list's transactions, in list order.</summary>
    public static byte[][] Flatten(IReadOnlyList<InclusionListV1> lists)
    {
        int count = 0;
        foreach (InclusionListV1 list in lists) count += list.Transactions?.Length ?? 0;
        byte[][] flattened = new byte[count][];
        int position = 0;
        foreach (InclusionListV1 list in lists)
        {
            list.Transactions?.CopyTo(flattened, position);
            position += list.Transactions?.Length ?? 0;
        }
        return flattened;
    }

    /// <summary>Decodes each list, dropping undecodable entries as EIP-7805 does for its flattened list.</summary>
    public static CommitteeInclusionList[] Decode(IReadOnlyList<InclusionListV1> lists)
    {
        CommitteeInclusionList[] decoded = new CommitteeInclusionList[lists.Count];
        for (int i = 0; i < decoded.Length; i++)
        {
            InclusionListV1 list = lists[i];
            decoded[i] = new CommitteeInclusionList(list.CommitteeIndex,
                TxsDecoder.DecodeTxs(list.Transactions ?? [], skipErrors: true).Transactions, list.RecursiveStark?.ToCommitment());
        }
        return decoded;
    }

    internal static void ApplyToBlock(BlockToProduce block, PayloadAttributes? attributes)
    {
        if (attributes?.InclusionLists is { } lists)
        {
            CommitteeInclusionList[] decoded = Decode(lists);
            block.InclusionLists = decoded;
            block.InclusionListTransactions = CommitteeInclusionList.Flatten(decoded);
        }
        else
        {
            block.InclusionListTransactions = attributes?.InclusionListTransactions is { } transactions
                ? TxsDecoder.DecodeTxs(transactions, skipErrors: true).Transactions : null;
            block.InclusionLists = null;
        }
        block.InclusionListProofInput = null;
    }

    /// <summary>Copies the lists, so later changes to the caller's buffers cannot alter a retained input.</summary>
    public static InclusionListV1[] Snapshot(IReadOnlyList<InclusionListV1> lists)
    {
        InclusionListV1[] copy = new InclusionListV1[lists.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            InclusionListV1 list = lists[i];
            byte[][] transactions = new byte[list.Transactions?.Length ?? 0][];
            for (int j = 0; j < transactions.Length; j++) transactions[j] = list.Transactions![j].AsSpan().ToArray();
            copy[i] = new InclusionListV1
            {
                CommitteeIndex = list.CommitteeIndex,
                Transactions = transactions,
                RecursiveStark = list.RecursiveStark is { } commitment
                    ? new RecursiveStarkV1 { StarkProofHash = commitment.StarkProofHash, DepsHash = commitment.DepsHash }
                    : null
            };
        }
        return copy;
    }

    /// <summary>Whether two inputs carry the same lists, positions and commitments.</summary>
    /// <param name="compareTransactions">Whether to compare transaction bytes too, not just how many each list holds.</param>
    public static bool AreEqual(IReadOnlyList<InclusionListV1>? left, IReadOnlyList<InclusionListV1>? right, bool compareTransactions = true)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
        {
            InclusionListV1 a = left[i], b = right[i];
            if (a.CommitteeIndex != b.CommitteeIndex || (a.Transactions?.Length ?? 0) != (b.Transactions?.Length ?? 0)) return false;
            for (int j = 0; compareTransactions && j < (a.Transactions?.Length ?? 0); j++)
                if (!a.Transactions![j].AsSpan().SequenceEqual(b.Transactions![j])) return false;
            if (a.RecursiveStark is null != b.RecursiveStark is null) return false;
            if (a.RecursiveStark is { } p && (p.StarkProofHash != b.RecursiveStark!.StarkProofHash || p.DepsHash != b.RecursiveStark.DepsHash))
                return false;
        }
        return true;
    }

    /// <summary>A digest binding every list's position, transactions and commitment, for payload IDs and verdict caches.</summary>
    public static ValueHash256 ComputeDigest(IReadOnlyList<InclusionListV1> lists)
    {
        KeccakHash hash = KeccakHash.Create();
        hash.Update("EIP8288 inclusion lists"u8);
        Span<byte> word = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(word, (ulong)lists.Count);
        hash.Update(word);
        foreach (InclusionListV1 list in lists)
        {
            BinaryPrimitives.WriteUInt64BigEndian(word, list.CommitteeIndex);
            hash.Update(word);
            BinaryPrimitives.WriteUInt64BigEndian(word, (ulong)(list.Transactions?.Length ?? 0));
            hash.Update(word);
            foreach (byte[] transaction in list.Transactions ?? []) hash.Update(ValueKeccak.Compute(transaction).Bytes);
            word.Clear();
            word[^1] = list.RecursiveStark is null ? (byte)0 : (byte)1;
            hash.Update(word);
            if (list.RecursiveStark is { } commitment)
            {
                hash.Update((commitment.StarkProofHash ?? Keccak.Zero).Bytes);
                hash.Update((commitment.DepsHash ?? Keccak.Zero).Bytes);
            }
        }
        return hash.GenerateValueHash();
    }
}
