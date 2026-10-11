// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Producers;

/// <summary>Engine API <c>RecursiveStarkV1</c>: an inclusion list's EIP-8288 <c>recursive_stark = [stark_proof, deps_hash]</c>.</summary>
public sealed class RecursiveStarkV1
{
    /// <summary><c>stark_proof</c>; empty exactly when the list's dependency set is empty.</summary>
    public byte[]? StarkProof { get; set; }

    /// <summary><c>deps_hash</c>, the commitment to the list's dependency union.</summary>
    public Hash256? DepsHash { get; set; }

    public RecursiveStark ToRecursiveStark() => new(StarkProof!, DepsHash!);
}

/// <summary>
/// Engine API <c>InclusionListV1</c>: one counted EIP-7805 inclusion list as EIP-8288's <c>[transactions, recursive_stark]</c>,
/// with the committee position that authenticated it.
/// </summary>
/// <remarks>
/// Carried by <c>engine_newPayloadV7</c> and <c>PayloadAttributesV6</c> once EIP-8288 and EIP-7805 are both active, in place of
/// the flattened <c>inclusionListTransactions</c>, so the execution layer can check each list's dependency proof and derive its
/// obligations separately.
/// </remarks>
public sealed class InclusionListV1
{
    /// <summary>Position of the inclusion-list committee member whose list this is.</summary>
    public ulong CommitteeIndex { get; set; }

    /// <summary>The list's EIP-2718 transaction encodings, in order.</summary>
    public byte[][]? Transactions { get; set; }

    /// <summary>The list's dependency proof, or <c>null</c> when it carries none; its dependency-free transactions still bind.</summary>
    public RecursiveStarkV1? RecursiveStark { get; set; }

    /// <summary>Checks the per-list Engine API bounds: at most one list per committee position, each within
    /// <c>MAX_TRANSACTIONS_BYTES_PER_INCLUSION_LIST</c> with nonempty entries, and well-formed proof fields.</summary>
    /// <returns>The violated bound, or <c>null</c>.</returns>
    public static string? Validate(InclusionListV1[]? lists)
    {
        if (lists is null) return "Inclusion lists must be set";
        if (lists.Length > Eip7805Constants.InclusionListCommitteeSize) return "Too many inclusion lists";
        Span<bool> seen = stackalloc bool[Eip7805Constants.InclusionListCommitteeSize];
        foreach (InclusionListV1? list in lists)
        {
            if (list?.Transactions is not { } transactions) return "Inclusion list transactions must be set";
            if (list.CommitteeIndex >= Eip7805Constants.InclusionListCommitteeSize) return "Inclusion list committee index out of range";
            if (seen[(int)list.CommitteeIndex]) return "Duplicate inclusion list committee index";
            seen[(int)list.CommitteeIndex] = true;
            if (transactions.Length > Eip7805Constants.MaxTransactionsPerInclusionList) return "Inclusion list exceeds the maximum number of transactions";
            long bytes = 0;
            foreach (byte[]? transaction in transactions)
            {
                if (transaction is not { Length: > 0 }) return "Inclusion list transactions must be nonempty";
                bytes += transaction.Length;
            }
            if (bytes > Eip7805Constants.MaxBytesPerInclusionList) return "Inclusion list exceeds MAX_TRANSACTIONS_BYTES_PER_INCLUSION_LIST";
            if (list.RecursiveStark is { } proof
                && (proof.StarkProof is null || proof.DepsHash is null || !proof.ToRecursiveStark().HasValidShape))
                return "Invalid inclusion-list recursive STARK";
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
                TxsDecoder.DecodeTxs(list.Transactions ?? [], skipErrors: true).Transactions, list.RecursiveStark?.ToRecursiveStark());
        }
        return decoded;
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
                RecursiveStark = list.RecursiveStark is { } proof
                    ? new RecursiveStarkV1 { StarkProof = proof.StarkProof?.AsSpan().ToArray(), DepsHash = proof.DepsHash }
                    : null
            };
        }
        return copy;
    }

    /// <summary>Whether two inputs carry the same lists, positions and proofs.</summary>
    /// <param name="left">One input.</param>
    /// <param name="right">The other input.</param>
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
            if (a.RecursiveStark is { } p && (p.DepsHash != b.RecursiveStark!.DepsHash
                || !p.StarkProof.AsSpan().SequenceEqual(b.RecursiveStark.StarkProof))) return false;
        }
        return true;
    }

    /// <summary>A digest binding every list's position, transactions and proof, for payload IDs and verdict caches.</summary>
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
            if (list.RecursiveStark is { } proof)
            {
                hash.Update((proof.DepsHash ?? Keccak.Zero).Bytes);
                hash.Update(ValueKeccak.Compute(proof.StarkProof ?? []).Bytes);
            }
        }
        return hash.GenerateValueHash();
    }
}
