// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Producers;

/// <summary>Engine API <c>RecursiveStarkV1</c>: an inclusion list's EIP-8288 <c>InclusionListProof</c>
/// <c>[stark_proof, proof_deps]</c>.</summary>
/// <remarks>An empty proof is <c>"0x"</c> with an empty <c>proofDeps</c>.</remarks>
public sealed class RecursiveStarkV1
{
    public static RecursiveStarkV1 Empty => new() { StarkProof = [], ProofDeps = [] };

    public byte[]? StarkProof { get; set; }

    /// <summary>The proof's dependencies, each a 96-byte dependency encoding, exactly as signed.</summary>
    public byte[][]? ProofDeps { get; set; }

    /// <summary>The bytes the proof counts against <c>MAX_IL_PROOF_BYTES</c>: the serialized SSZ <c>InclusionListProof</c>
    /// container, two offsets and both fields, plus the four-byte offset it adds to the signed <c>InclusionList</c>.</summary>
    internal long SszLength => Eip8288Constants.IlProofBytes(StarkProof?.Length ?? 0, ProofDeps?.Length ?? 0);
}

/// <summary>
/// Engine API <c>InclusionListV1</c>: one counted EIP-7805 inclusion list with the EIP-8288 <c>InclusionListProof</c> appended to
/// the signed list.
/// </summary>
/// <remarks>
/// Carried by <c>engine_newPayloadV7</c> and <c>PayloadAttributesV6</c> once EIP-8288 and EIP-7805 are both active, in place of the
/// flattened <c>inclusionListTransactions</c> and in committee-position order, so the execution layer derives each list's
/// obligations separately (EIPs#12475). The consensus layer passes the signed proof bytes unchanged, even when invalid.
/// </remarks>
public sealed class InclusionListV1
{
    /// <summary>The list's EIP-2718 transaction encodings, in order.</summary>
    public byte[][]? Transactions { get; set; }

    /// <summary>The list's proof; an empty one obligates only its dependency-free transactions.</summary>
    public RecursiveStarkV1? RecursiveStark { get; set; }

    /// <summary>Checks the per-list Engine API bounds: at most <c>IL_COMMITTEE_SIZE</c> lists, each with transactions within
    /// <c>MAX_TRANSACTIONS_BYTES_PER_INCLUSION_LIST</c>, with a proof whose <c>proofDeps</c> entries are 96 bytes and whose SSZ
    /// container, with its added <c>InclusionList</c> offset, fits <c>MAX_IL_PROOF_BYTES</c>.</summary>
    /// <remarks>Proof bytes never count against the transaction-byte limit: from EIP-8288's activation a list carrying a proof
    /// may exceed EIP-7805's list size limit by at most <c>MAX_IL_PROOF_BYTES</c>.</remarks>
    /// <remarks>An undecodable entry or an invalid proof is left to per-list processing: the entry contributes nothing and the
    /// proof obligates nothing (EIPs#12475), so one committee member cannot fail the request.</remarks>
    /// <returns>The violated bound, or <c>null</c>.</returns>
    public static string? Validate(InclusionListV1[]? lists)
    {
        if (lists is null) return "Inclusion lists must be set";
        if (lists.Length > Eip7805Constants.InclusionListCommitteeSize) return "Too many inclusion lists";
        foreach (InclusionListV1? list in lists)
        {
            if (list?.Transactions is not { } transactions) return "Inclusion list transactions must be set";
            if (transactions.Length > Eip7805Constants.MaxTransactionsPerInclusionList) return "Inclusion list exceeds the maximum number of transactions";
            long bytes = 0;
            foreach (byte[]? transaction in transactions)
            {
                if (transaction is null) return "Inclusion list transactions must be set";
                bytes += transaction.Length;
            }
            if (bytes > Eip7805Constants.MaxBytesPerInclusionList) return "Inclusion list exceeds MAX_TRANSACTIONS_BYTES_PER_INCLUSION_LIST";
            if (list.RecursiveStark is not { StarkProof: not null, ProofDeps: { } proofDeps } proof)
                return "Inclusion list recursiveStark must set starkProof and proofDeps";
            foreach (byte[]? dependency in proofDeps)
                if (dependency is not { Length: Eip8288Constants.DependencyTripleLength })
                    return "Inclusion list proofDeps entries must be 96-byte dependency encodings";
            if (proof.SszLength > Eip8288Constants.MaxIlProofBytes) return "Inclusion list recursiveStark exceeds MAX_IL_PROOF_BYTES";
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
            decoded[i] = ToCommitteeList(lists[i], TxsDecoder.DecodeTxs(lists[i].Transactions ?? [], skipErrors: true).Transactions);
        return decoded;
    }

    /// <summary>Pairs a list's decoded <paramref name="transactions"/> with its proof.</summary>
    public static CommitteeInclusionList ToCommitteeList(InclusionListV1 list, Transaction[] transactions)
        => new(transactions, list.RecursiveStark?.StarkProof ?? [], list.RecursiveStark?.ProofDeps ?? []);

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
            copy[i] = new InclusionListV1
            {
                Transactions = Copy(list.Transactions ?? []),
                RecursiveStark = new RecursiveStarkV1
                {
                    StarkProof = (list.RecursiveStark?.StarkProof ?? []).AsSpan().ToArray(),
                    ProofDeps = Copy(list.RecursiveStark?.ProofDeps ?? [])
                }
            };
        }
        return copy;

        static byte[][] Copy(byte[][] items)
        {
            byte[][] result = new byte[items.Length][];
            for (int i = 0; i < result.Length; i++) result[i] = items[i].AsSpan().ToArray();
            return result;
        }
    }

    /// <summary>Whether two inputs carry the same lists and proofs.</summary>
    /// <param name="compareTransactions">Whether to compare transaction bytes too, not just how many each list holds.</param>
    public static bool AreEqual(IReadOnlyList<InclusionListV1>? left, IReadOnlyList<InclusionListV1>? right, bool compareTransactions = true)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
        {
            InclusionListV1 a = left[i], b = right[i];
            if ((a.Transactions?.Length ?? 0) != (b.Transactions?.Length ?? 0)) return false;
            for (int j = 0; compareTransactions && j < (a.Transactions?.Length ?? 0); j++)
                if (!a.Transactions![j].AsSpan().SequenceEqual(b.Transactions![j])) return false;
            if (!(a.RecursiveStark?.StarkProof ?? []).AsSpan().SequenceEqual(b.RecursiveStark?.StarkProof ?? [])) return false;
            byte[][] aDeps = a.RecursiveStark?.ProofDeps ?? [], bDeps = b.RecursiveStark?.ProofDeps ?? [];
            if (aDeps.Length != bDeps.Length) return false;
            for (int j = 0; j < aDeps.Length; j++)
                if (!aDeps[j].AsSpan().SequenceEqual(bDeps[j])) return false;
        }
        return true;
    }

    /// <summary>A digest binding every list's transactions and proof, for payload IDs.</summary>
    public static ValueHash256 ComputeDigest(IReadOnlyList<InclusionListV1> lists)
    {
        KeccakHash hash = KeccakHash.Create();
        hash.Update("EIP8288 inclusion lists"u8);
        Span<byte> word = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(word, (ulong)lists.Count);
        hash.Update(word);
        foreach (InclusionListV1 list in lists)
        {
            BinaryPrimitives.WriteUInt64BigEndian(word, (ulong)(list.Transactions?.Length ?? 0));
            hash.Update(word);
            foreach (byte[] transaction in list.Transactions ?? []) hash.Update(ValueKeccak.Compute(transaction).Bytes);
            hash.Update(ValueKeccak.Compute(list.RecursiveStark?.StarkProof ?? []).Bytes);
            BinaryPrimitives.WriteUInt64BigEndian(word, (ulong)(list.RecursiveStark?.ProofDeps?.Length ?? 0));
            hash.Update(word);
            foreach (byte[] dependency in list.RecursiveStark?.ProofDeps ?? []) hash.Update(dependency);
        }
        return hash.GenerateValueHash();
    }
}
