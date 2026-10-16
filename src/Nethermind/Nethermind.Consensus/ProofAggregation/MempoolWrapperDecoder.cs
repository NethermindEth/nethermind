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
/// RLP codec for the EIP-8288 mempool wrapper <c>[transactions, mode, [deps, proof_content]]</c>, the hash-only
/// wrapper EIP-8437 announces and retrieves. Transaction entries are 32-byte hashes in strictly ascending order; <c>deps</c> lists 96-byte triples; <c>proof_content</c> is one proof
/// per dependency for mode 0 and the <c>stark_proof</c> for mode 1, whose public input is derived from <c>deps</c>.
/// </summary>
public sealed class MempoolWrapperDecoder : RlpDecoder<MempoolWrapper>
{
    public static readonly MempoolWrapperDecoder Instance = new();
    /// <summary>Mode-0 dependency and witness count bound: the direct per-scheme limits together.</summary>
    private const int MaxDirectDependencies = Eip8288Constants.MaxLeanSigDepsPerDirectWrapper + Eip8288Constants.MaxLeanStarkDepsPerDirectWrapper;

    protected override MempoolWrapper DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int length = decoderContext.ReadSequenceLength();
        int check = length + decoderContext.Position;
        if (Rlp.LengthOfSequence(length) > Eip8288Constants.MaxWrapperBytes)
            throw new RlpException("Proof wrapper exceeds MAX_WRAPPER_BYTES.");

        int txCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
        List<WrapperTransaction> transactions = [];
        Hash256? previous = null;
        while (decoderContext.Position < txCheck)
        {
            if (transactions.Count == LeanProofStore.MaxWrapperTransactions)
                throw new RlpException("Proof wrapper exceeds the transaction count limit.");
            Hash256 hash = decoderContext.DecodeKeccak()
                ?? throw new RlpException("Proof wrapper hash entry must carry 32 bytes.");
            if (previous is not null && previous.Bytes.SequenceCompareTo(hash.Bytes) >= 0)
                throw new RlpException("Proof wrapper transactions must be in strictly ascending hash order.");
            previous = hash;
            transactions.Add(new WrapperTransaction(hash));
        }

        decoderContext.Check(txCheck);
        if (transactions.Count == 0) throw new RlpException("Proof wrapper must carry at least one transaction.");
        byte mode = decoderContext.DecodeByte();
        if (mode is not (MempoolWrapper.ModeDirect or MempoolWrapper.ModeRecursive))
            throw new RlpException(MempoolWrapperValidator.UnknownMode);
        // Counts are bounded per mode before allocation: direct witnesses by the mode-0 limits, aggregates by MAX_DEPS_PER_AGGREGATE_WRAPPER.
        int maxDependencies = mode == MempoolWrapper.ModeDirect ? MaxDirectDependencies : Eip8288Constants.MaxDepsPerAggregateWrapper;

        int contentCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
        int depsCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
        List<FrameDependency> deps = [];
        byte[]? previousTriple = null;
        while (decoderContext.Position < depsCheck)
        {
            if (deps.Count == maxDependencies) throw new RlpException("Proof wrapper exceeds the dependency count limit.");
            byte[] triple = decoderContext.DecodeByteArray(RlpLimit.For<MempoolWrapper>(Eip8288Constants.DependencyTripleLength, nameof(MempoolWrapper.Deps)));
            if (triple.Length != Eip8288Constants.DependencyTripleLength || !triple.AsSpan(0, 31).IsZero()
                || !Eip8288Dependencies.IsAcceptedScheme(triple[31]))
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
                if (proofs.Count == MaxDirectDependencies)
                    throw new RlpException("Proof wrapper exceeds the witness count limit.");
                proofs.Add(decoderContext.DecodeByteArray(RlpLimit.For<RecursiveStark>(Eip8288Constants.MaxProofBytes, nameof(MempoolWrapper.Proofs))));
            }
            decoderContext.Check(proofsCheck);
        }
        else if (mode == MempoolWrapper.ModeRecursive)
        {
            byte[] starkProof = decoderContext.DecodeByteArray(RlpLimit.For<RecursiveStark>(Eip8288Constants.MaxAggregateProofBytes, nameof(RecursiveStark.StarkProof)));
            recursiveStark = new RecursiveStark(starkProof, new Hash256(Eip8288Dependencies.ComputeDepsHash(deps)));
        }
        else
        {
            throw new RlpException(MempoolWrapperValidator.UnknownMode);
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

    public override void Encode<TWriter>(ref TWriter writer, MempoolWrapper item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        _ = GetLength(item, rlpBehaviors);
        int txContentLength = GetTransactionsContentLength(item);
        byte[] depsBytes = Eip8288Dependencies.Serialize(item.Deps);
        (int contentContentLength, int innerContentLength) = GetContentLengths(item);

        writer.StartSequence(Rlp.LengthOfSequence(txContentLength) + Rlp.LengthOf((ulong)item.Mode) + Rlp.LengthOfSequence(contentContentLength));

        writer.StartSequence(txContentLength);
        foreach (WrapperTransaction transaction in item.Transactions)
            writer.Encode(GetHash(transaction));

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
        int txContentLength = GetTransactionsContentLength(item);
        (int contentContentLength, _) = GetContentLengths(item);

        int length = Rlp.LengthOfSequence(
            Rlp.LengthOfSequence(txContentLength)
            + Rlp.LengthOf((ulong)item.Mode)
            + Rlp.LengthOfSequence(contentContentLength));
        if (length > Eip8288Constants.MaxWrapperBytes) throw new RlpException("Proof wrapper exceeds MAX_WRAPPER_BYTES.");
        return length;
    }

    private static int GetTransactionsContentLength(MempoolWrapper item)
    {
        if (item.Transactions.Count is 0 or > Eip8288Constants.MaxTxsPerWrapper)
            throw new RlpException(MempoolWrapperValidator.TooManyTransactions);
        Hash256? previous = null;
        foreach (WrapperTransaction transaction in item.Transactions)
        {
            Hash256 hash = GetHash(transaction);
            if (previous is not null && previous.Bytes.SequenceCompareTo(hash.Bytes) >= 0)
                throw new RlpException("Proof wrapper transactions must be in strictly ascending hash order.");
            previous = hash;
        }
        return item.Transactions.Count * Rlp.LengthOfByteString(Hash256.Size, 0);
    }

    private static Hash256 GetHash(WrapperTransaction transaction) => transaction.Hash
        ?? transaction.Full?.Hash ?? transaction.Full?.CalculateHash()
        ?? throw new RlpException("Proof wrapper transaction has no hash.");

    private static (int ContentContentLength, int InnerContentLength) GetContentLengths(MempoolWrapper item)
    {
        if (item.Mode is not (MempoolWrapper.ModeDirect or MempoolWrapper.ModeRecursive))
            throw new RlpException(MempoolWrapperValidator.UnknownMode);
        int maximumDependencies = item.Mode == MempoolWrapper.ModeDirect ? MaxDirectDependencies : Eip8288Constants.MaxDepsPerAggregateWrapper;
        if (item.Deps.Count > maximumDependencies) throw new RlpException("Proof wrapper exceeds the dependency count limit.");
        int depsLength = Rlp.LengthOfSequence(item.Deps.Count * Rlp.LengthOfByteString(Eip8288Constants.DependencyTripleLength, 0));
        if (item.Mode == MempoolWrapper.ModeDirect)
        {
            if (item.Proofs is { Count: > MaxDirectDependencies }) throw new RlpException("Proof wrapper exceeds the witness count limit.");
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
