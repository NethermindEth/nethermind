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
/// order; <c>deps</c> lists 96-byte triples; <c>proof_content</c> is one proof per dependency for mode 0, the recursive
/// STARK for mode 1, whose public input is derived from <c>deps</c>, and for EIP-8437's mode 2 one entry per dependency, either
/// an ordinary witness or a raw <c>[public-key, message, signature]</c> list.
/// </summary>
/// <remarks>Decoding enforces the encoding rules: <c>MAX_WRAPPER_BYTES</c>, transaction ordering, well-formed 96-byte dependency
/// triples of a defined scheme and, in modes 0 and 2, one witness per dependency (EIP-8437). Dependency and proof rules, including
/// the dependency list's order and the mode-0 limits, are left to <see cref="MempoolWrapperValidator"/>, so EIP-8437 discards a
/// wrapper breaking them without penalizing its sender. Mode 2's framing is encoding: raw entries only for leanSTARK dependencies,
/// each within <see cref="MempoolWrapper.MaxRawWitnessBytes"/> with a 32-byte message, at least one and at most
/// <see cref="MempoolWrapper.MaxRawEntries"/> of them, and at most <see cref="MempoolWrapper.MaxRawTransactions"/> transactions.</remarks>
public sealed class MempoolWrapperDecoder : RlpDecoder<MempoolWrapper>
{
    public static readonly MempoolWrapperDecoder Instance = new();

    /// <summary>The pooled encoding of a wrapped transaction: its typed envelope, with any blob network wrapper.</summary>
    public const RlpBehaviors TransactionBehaviors = RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping;

    public const string MalformedTransaction = "Proof wrapper transaction is not a pooled EIP-8141 transaction.";
    public const string TransactionReference = "Proof wrapper transaction is an unresolved hash reference.";
    public const string MalformedRawWitness = "Mode 2 wrapper has a malformed raw witness.";
    public const string WitnessCountMismatch = "Proof wrapper witness count does not match its dependencies.";
    public const string TooManyRawItems = "Mode 2 wrapper exceeds its transaction or raw-entry count.";

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
        if (mode is not (MempoolWrapper.ModeDirect or MempoolWrapper.ModeRecursive or MempoolWrapper.ModeRaw))
            throw new RlpException(MempoolWrapperValidator.UnknownMode);
        if (mode == MempoolWrapper.ModeRaw && transactions.Count > MempoolWrapper.MaxRawTransactions) throw new RlpException(TooManyRawItems);

        int contentCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
        int depsCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
        List<FrameDependency> deps = [];
        while (decoderContext.Position < depsCheck)
        {
            // Unreachable within MAX_WRAPPER_BYTES; bounds the list before allocation.
            if (deps.Count == Eip8288Constants.MaxWrapperDependencies) throw new RlpException("Proof wrapper exceeds MAX_WRAPPER_BYTES.");
            byte[] triple = decoderContext.DecodeByteArray(RlpLimit.For<MempoolWrapper>(Eip8288Constants.DependencyTripleLength, nameof(MempoolWrapper.Deps)));
            if (triple.Length != Eip8288Constants.DependencyTripleLength || !triple.AsSpan(0, 31).IsZero()
                || !Eip8288Dependencies.IsKnownScheme(triple[31]))
                throw new RlpException("Invalid dependency encoding.");
            deps.AddRange(Eip8288Dependencies.Parse(triple));
        }
        decoderContext.Check(depsCheck);

        List<byte[]>? proofs = null;
        List<RawSignature?>? raws = null;
        RecursiveStark? recursiveStark = null;
        if (mode == MempoolWrapper.ModeRaw)
        {
            (proofs, raws) = DecodeRawContent(ref decoderContext, deps);
        }
        else if (mode == MempoolWrapper.ModeDirect)
        {
            int proofsCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
            proofs = [];
            // Each witness takes at least one byte of a wrapper within MAX_WRAPPER_BYTES, which bounds the list.
            while (decoderContext.Position < proofsCheck)
            {
                proofs.Add(decoderContext.DecodeByteArray(RlpLimit.For<RecursiveStark>(Eip8288Constants.MaxProofBytes, nameof(MempoolWrapper.Proofs))));
            }
            decoderContext.Check(proofsCheck);
            if (proofs.Count != deps.Count) throw new RlpException(WitnessCountMismatch);
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
            RawWitnesses = raws,
            RecursiveStark = recursiveStark,
        };
    }

    private static (List<byte[]> Proofs, List<RawSignature?> Raws) DecodeRawContent(ref RlpReader decoderContext, List<FrameDependency> deps)
    {
        int proofsCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
        List<byte[]> proofs = [];
        List<RawSignature?> raws = [];
        int rawCount = 0;
        while (decoderContext.Position < proofsCheck)
        {
            if (proofs.Count == deps.Count) throw new RlpException(WitnessCountMismatch);
            if (!decoderContext.IsSequenceNext())
            {
                proofs.Add(decoderContext.DecodeByteArray(RlpLimit.For<RecursiveStark>(Eip8288Constants.MaxProofBytes, nameof(MempoolWrapper.Proofs))));
                raws.Add(null);
                continue;
            }
            int start = decoderContext.Position;
            int entryCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
            if (entryCheck - start > MempoolWrapper.MaxRawWitnessBytes || deps[proofs.Count].Scheme != Eip8288Constants.LeanStarkScheme)
                throw new RlpException(MalformedRawWitness);
            RlpLimit limit = RlpLimit.For<RawSignature>(MempoolWrapper.MaxRawWitnessBytes, nameof(MempoolWrapper.RawWitnesses));
            byte[] publicKey = decoderContext.DecodeByteArray(limit);
            byte[] message = decoderContext.DecodeByteArray(limit);
            byte[] signature = decoderContext.DecodeByteArray(limit);
            decoderContext.Check(entryCheck);
            if (message.Length != ValueHash256.MemorySize) throw new RlpException(MalformedRawWitness);
            if (++rawCount > MempoolWrapper.MaxRawEntries) throw new RlpException(TooManyRawItems);
            proofs.Add([]);
            raws.Add(new RawSignature(publicKey, new ValueHash256(message), signature));
        }
        decoderContext.Check(proofsCheck);
        if (proofs.Count != deps.Count) throw new RlpException(WitnessCountMismatch);
        if (rawCount == 0) throw new RlpException(MalformedRawWitness);
        return (proofs, raws);
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
        else if (item.Mode == MempoolWrapper.ModeRaw)
        {
            writer.StartSequence(innerContentLength);
            for (int i = 0; i < item.Proofs!.Count; i++)
            {
                if (item.RawWitnesses![i] is { } raw)
                {
                    writer.StartSequence(RawContentLength(raw));
                    writer.Encode(raw.PublicKey);
                    writer.Encode(raw.Message.Bytes);
                    writer.Encode(raw.Signature);
                }
                else writer.Encode(item.Proofs[i]);
            }
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
        if (item.Mode is not (MempoolWrapper.ModeDirect or MempoolWrapper.ModeRecursive or MempoolWrapper.ModeRaw))
            throw new RlpException(MempoolWrapperValidator.UnknownMode);
        if (item.Deps.Count > Eip8288Constants.MaxWrapperDependencies) throw new RlpException("Proof wrapper exceeds MAX_WRAPPER_BYTES.");
        int depsLength = Rlp.LengthOfSequence(item.Deps.Count * Rlp.LengthOfByteString(Eip8288Constants.DependencyTripleLength, 0));
        if (item.Mode == MempoolWrapper.ModeRaw)
        {
            int rawLength = RawInnerLength(item);
            return (depsLength + Rlp.LengthOfSequence(rawLength), rawLength);
        }
        if (item.Mode == MempoolWrapper.ModeDirect)
        {
            if (item.Proofs?.Count != item.Deps.Count) throw new RlpException(WitnessCountMismatch);
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

    private static int RawContentLength(RawSignature raw) =>
        Rlp.LengthOf(raw.PublicKey) + Rlp.LengthOfByteString(ValueHash256.MemorySize, 0) + Rlp.LengthOf(raw.Signature);

    /// <summary>The content length of a mode-2 entry list, checking the framing the decoder enforces.</summary>
    private static int RawInnerLength(MempoolWrapper item)
    {
        IReadOnlyList<byte[]>? proofs = item.Proofs;
        IReadOnlyList<RawSignature?>? raws = item.RawWitnesses;
        if (proofs is null || raws is null || proofs.Count != item.Deps.Count || raws.Count != proofs.Count)
            throw new RlpException(WitnessCountMismatch);
        if (item.Transactions.Count > MempoolWrapper.MaxRawTransactions) throw new RlpException(TooManyRawItems);
        long length = 0;
        int rawCount = 0;
        for (int i = 0; i < proofs.Count; i++)
        {
            if (raws[i] is { } raw)
            {
                int entry = Rlp.LengthOfSequence(RawContentLength(raw));
                if (entry > MempoolWrapper.MaxRawWitnessBytes || item.Deps[i].Scheme != Eip8288Constants.LeanStarkScheme)
                    throw new RlpException(MalformedRawWitness);
                length += entry;
                if (++rawCount > MempoolWrapper.MaxRawEntries) throw new RlpException(TooManyRawItems);
            }
            else
            {
                if (proofs[i].Length > Eip8288Constants.MaxProofBytes) throw new RlpException("Proof wrapper witness exceeds the proof size limit.");
                length += Rlp.LengthOf(proofs[i]);
            }
            if (length > Eip8288Constants.MaxWrapperBytes) throw new RlpException("Proof wrapper exceeds MAX_WRAPPER_BYTES.");
        }
        if (rawCount == 0) throw new RlpException(MalformedRawWitness);
        return (int)length;
    }
}
