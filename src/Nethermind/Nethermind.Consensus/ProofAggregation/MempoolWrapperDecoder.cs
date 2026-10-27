// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>
/// RLP codec for the canonical EIP-8288 mempool wrapper <c>[transactions, mode, [deps, proof_content]]</c>. Transactions are
/// RLP byte strings holding full EIP-8141 transactions in their pooled network form, in strictly ascending transaction-hash
/// order; <c>deps</c> lists 96-byte triples; <c>proof_content</c> is one proof per dependency for mode 0 and the recursive
/// STARK for mode 1, whose public input is derived from <c>deps</c>.
/// </summary>
/// <remarks>Decoding enforces the fork-independent encoding rules: <c>MAX_WRAPPER_BYTES</c>, ordering, the dependency count
/// bound shared by both modes and the mode-0 witness count. The dependency rules themselves are left to
/// <see cref="MempoolWrapperValidator"/>.</remarks>
public sealed class MempoolWrapperDecoder : RlpDecoder<MempoolWrapper>
{
    public static readonly MempoolWrapperDecoder Instance = new();

    /// <summary>The pooled encoding of a wrapped transaction: its typed envelope, with any blob network wrapper.</summary>
    public const RlpBehaviors TransactionBehaviors = RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping;

    public const string MalformedTransaction = "Proof wrapper transaction is not a pooled EIP-8141 transaction.";
    public const string TransactionReference = "Proof wrapper transaction is an unresolved hash reference.";

    protected override MempoolWrapper DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int length = decoderContext.ReadSequenceLength();
        int check = length + decoderContext.Position;
        if (Rlp.LengthOfSequence(length) > Eip8288Constants.MaxWrapperBytes)
            throw new RlpException("Proof wrapper exceeds MAX_WRAPPER_BYTES.");

        int txCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
        List<Transaction> transactions = [];
        Hash256? previous = null;
        while (decoderContext.Position < txCheck)
        {
            if (decoderContext.IsSequenceNext()) throw new RlpException(TransactionReference);
            Transaction transaction = DecodeTransaction(decoderContext.DecodeByteArraySpan());
            if (previous is not null && previous.Bytes.SequenceCompareTo(transaction.Hash!.Bytes) >= 0)
                throw new RlpException("Proof wrapper transactions must be in strictly ascending hash order.");
            previous = transaction.Hash;
            transactions.Add(transaction);
        }

        decoderContext.Check(txCheck);
        if (transactions.Count == 0) throw new RlpException("Proof wrapper must carry at least one transaction.");
        byte mode = decoderContext.DecodeByte();
        if (mode is not (MempoolWrapper.ModeDirect or MempoolWrapper.ModeRecursive))
            throw new RlpException(MempoolWrapperValidator.UnknownMode);

        int contentCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
        int depsCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
        List<FrameDependency> deps = [];
        byte[]? previousTriple = null;
        while (decoderContext.Position < depsCheck)
        {
            // Both modes share the per-scheme limits, so their sum bounds the list before allocation.
            if (deps.Count == Eip8288Constants.MaxDepsPerWrapper) throw new RlpException("Proof wrapper exceeds the dependency count limit.");
            byte[] triple = decoderContext.DecodeByteArray(RlpLimit.For<MempoolWrapper>(Eip8288Constants.DependencyTripleLength, nameof(MempoolWrapper.Deps)));
            if (triple.Length != Eip8288Constants.DependencyTripleLength || !triple.AsSpan(0, 31).IsZero()
                || !Eip8288Dependencies.IsKnownScheme(triple[31]))
                throw new RlpException("Invalid dependency encoding.");
            if (previousTriple is not null && previousTriple.AsSpan().SequenceCompareTo(triple) >= 0)
                throw new RlpException("Proof wrapper dependencies must be sorted and deduplicated.");
            previousTriple = triple;
            deps.AddRange(Eip8288Dependencies.Parse(triple));
        }
        decoderContext.Check(depsCheck);
        if (deps.Count == 0) throw new RlpException(MempoolWrapperValidator.EmptyAggregate);

        List<byte[]>? proofs = null;
        RecursiveStark? recursiveStark = null;
        if (mode == MempoolWrapper.ModeDirect)
        {
            int proofsCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
            proofs = [];
            while (decoderContext.Position < proofsCheck)
            {
                if (proofs.Count == Eip8288Constants.MaxDepsPerWrapper)
                    throw new RlpException("Proof wrapper exceeds the witness count limit.");
                proofs.Add(decoderContext.DecodeByteArray(RlpLimit.For<RecursiveStark>(Eip8288Constants.MaxProofBytes, nameof(MempoolWrapper.Proofs))));
            }
            decoderContext.Check(proofsCheck);
        }
        else
        {
            byte[] starkProof = decoderContext.DecodeByteArray(RlpLimit.For<RecursiveStark>(Eip8288Constants.MaxAggregateProofBytes, nameof(RecursiveStark.StarkProof)));
            recursiveStark = new RecursiveStark(starkProof, new Hash256(Eip8288Dependencies.ComputeDepsHash(deps)));
        }
        decoderContext.Check(contentCheck);

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            decoderContext.Check(check);
        }

        return new MempoolWrapper
        {
            Transactions = transactions,
            Mode = mode,
            Deps = deps,
            Proofs = proofs,
            RecursiveStark = recursiveStark,
        };
    }

    /// <summary>Decodes one wrapped transaction, with its consensus hash, which excludes any blob network wrapper.</summary>
    public static Transaction DecodeTransaction(ReadOnlySpan<byte> encoded)
    {
        Transaction transaction;
        try
        {
            transaction = TxDecoder.Instance.DecodeCompleteNotNull(encoded, TransactionBehaviors);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new RlpException(MalformedTransaction, exception);
        }
        if (!transaction.SupportsFrames) throw new RlpException(MempoolWrapperValidator.NotFrameTransaction);
        transaction.Hash ??= transaction.CalculateHash();
        return transaction;
    }

    /// <summary>Encodes one wrapped transaction in its pooled network form.</summary>
    public static byte[] EncodeTransaction(Transaction transaction) => TxDecoder.Instance.Encode(transaction, TransactionBehaviors).Bytes;

    public override void Encode<TWriter>(ref TWriter writer, MempoolWrapper item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        byte[][] encodedTransactions = EncodeTransactions(item, out int txContentLength);
        byte[] depsBytes = Eip8288Dependencies.Serialize(item.Deps);
        (int contentContentLength, int innerContentLength) = GetContentLengths(item);
        CheckLength(txContentLength, item.Mode, contentContentLength);

        writer.StartSequence(Rlp.LengthOfSequence(txContentLength) + Rlp.LengthOf((ulong)item.Mode) + Rlp.LengthOfSequence(contentContentLength));

        writer.StartSequence(txContentLength);
        foreach (byte[] transaction in encodedTransactions)
            writer.Encode(transaction);

        writer.Encode((ulong)item.Mode);

        writer.StartSequence(contentContentLength);
        writer.StartSequence(item.Deps.Count * Rlp.LengthOfByteString(Eip8288Constants.DependencyTripleLength, 0));
        for (int i = 0; i < item.Deps.Count; i++)
            writer.Encode(depsBytes.AsSpan(i * Eip8288Constants.DependencyTripleLength, Eip8288Constants.DependencyTripleLength));
        if (item.Mode == MempoolWrapper.ModeDirect)
        {
            writer.StartSequence(innerContentLength);
            foreach (byte[] proof in item.Proofs!) writer.Encode(proof);
        }
        else
        {
            writer.Encode(item.RecursiveStark!.StarkProof);
        }
    }

    public override int GetLength(MempoolWrapper item, RlpBehaviors rlpBehaviors)
    {
        _ = EncodeTransactions(item, out int txContentLength);
        (int contentContentLength, _) = GetContentLengths(item);
        return CheckLength(txContentLength, item.Mode, contentContentLength);
    }

    private static int CheckLength(int txContentLength, byte mode, int contentContentLength)
    {
        long length = Rlp.LengthOfSequence(Rlp.LengthOfSequence(txContentLength) + Rlp.LengthOf((ulong)mode) + Rlp.LengthOfSequence(contentContentLength));
        if (length > Eip8288Constants.MaxWrapperBytes) throw new RlpException("Proof wrapper exceeds MAX_WRAPPER_BYTES.");
        return (int)length;
    }

    private static byte[][] EncodeTransactions(MempoolWrapper item, out int contentLength)
    {
        if (item.Transactions.Count == 0) throw new RlpException("Proof wrapper must carry at least one transaction.");
        byte[][] encoded = new byte[item.Transactions.Count][];
        long length = 0;
        Hash256? previous = null;
        for (int i = 0; i < encoded.Length; i++)
        {
            Transaction transaction = item.Transactions[i];
            if (!transaction.SupportsFrames) throw new RlpException(MempoolWrapperValidator.NotFrameTransaction);
            Hash256 hash = transaction.Hash ?? transaction.CalculateHash();
            if (previous is not null && previous.Bytes.SequenceCompareTo(hash.Bytes) >= 0)
                throw new RlpException("Proof wrapper transactions must be in strictly ascending hash order.");
            previous = hash;
            encoded[i] = EncodeTransaction(transaction);
            length += Rlp.LengthOf(encoded[i]);
            if (length > Eip8288Constants.MaxWrapperBytes) throw new RlpException("Proof wrapper exceeds MAX_WRAPPER_BYTES.");
        }
        contentLength = (int)length;
        return encoded;
    }

    private static (int ContentContentLength, int InnerContentLength) GetContentLengths(MempoolWrapper item)
    {
        if (item.Mode is not (MempoolWrapper.ModeDirect or MempoolWrapper.ModeRecursive))
            throw new RlpException(MempoolWrapperValidator.UnknownMode);
        if (item.Deps.Count > Eip8288Constants.MaxDepsPerWrapper) throw new RlpException("Proof wrapper exceeds the dependency count limit.");
        int depsLength = Rlp.LengthOfSequence(item.Deps.Count * Rlp.LengthOfByteString(Eip8288Constants.DependencyTripleLength, 0));
        if (item.Mode == MempoolWrapper.ModeDirect)
        {
            if (item.Proofs is { Count: > Eip8288Constants.MaxDepsPerWrapper }) throw new RlpException("Proof wrapper exceeds the witness count limit.");
            int innerContentLength = 0;
            foreach (byte[] proof in item.Proofs ?? [])
            {
                if (proof.Length > Eip8288Constants.MaxProofBytes) throw new RlpException("Proof wrapper witness exceeds the proof size limit.");
                innerContentLength += Rlp.LengthOf(proof);
                if (innerContentLength > Eip8288Constants.MaxWrapperBytes) throw new RlpException("Proof wrapper exceeds MAX_WRAPPER_BYTES.");
            }
            return (depsLength + Rlp.LengthOfSequence(innerContentLength), innerContentLength);
        }

        if (item.RecursiveStark?.StarkProof is not { } aggregate || aggregate.Length > Eip8288Constants.MaxAggregateProofBytes)
            throw new RlpException("Proof wrapper aggregate exceeds the proof size limit.");
        return (depsLength + Rlp.LengthOf(aggregate), 0);
    }
}
