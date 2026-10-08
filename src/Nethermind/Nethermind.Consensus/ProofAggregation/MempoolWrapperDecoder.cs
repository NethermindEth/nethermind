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
/// RLP codec for the EIP-8288 mempool wrapper <c>[transactions, mode, [deps, proof_content]]</c>, the EIP-8437
/// kind-1 body. A transaction entry is <c>[0, transaction]</c> or, when already broadcast, <c>[1, hash]</c>, in
/// strictly ascending transaction-hash order; <c>deps</c> lists 96-byte triples; <c>proof_content</c> is one proof
/// per dependency for mode 0 and the <c>stark_proof</c> for mode 1, whose public input is derived from <c>deps</c>.
/// </summary>
public sealed class MempoolWrapperDecoder : RlpDecoder<MempoolWrapper>
{
    public static readonly MempoolWrapperDecoder Instance = new();
    private const byte FullEntry = 0;
    private const byte HashEntry = 1;
    private const int MaxWrapperDependencies = Eip8288Constants.MaxLeanSigDepsPerWrapper + Eip8288Constants.MaxLeanStarkDepsPerWrapper;

    protected override MempoolWrapper DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int length = decoderContext.ReadSequenceLength();
        int check = length + decoderContext.Position;

        int txCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
        List<WrapperTransaction> transactions = [];
        Hash256? previous = null;
        while (decoderContext.Position < txCheck)
        {
            if (transactions.Count == LeanProofStore.MaxWrapperTransactions)
                throw new RlpException("Proof wrapper exceeds the transaction count limit.");
            int entryCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
            WrapperTransaction entry = decoderContext.DecodeByte() switch
            {
                FullEntry => new WrapperTransaction(TxDecoder.Instance.DecodeCompleteNotNull(
                    decoderContext.DecodeByteArray(RlpLimit.For<MempoolWrapper>(LeanProofStore.MaxWrapperBytes, nameof(MempoolWrapper.Transactions))),
                    RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping)),
                HashEntry => new WrapperTransaction(decoderContext.DecodeKeccak() ?? throw new RlpException("Proof wrapper hash entry must carry 32 bytes.")),
                _ => throw new RlpException("Unknown proof wrapper transaction entry tag.")
            };
            decoderContext.Check(entryCheck);
            Hash256 hash = entry.Hash ?? entry.Full!.Hash ?? entry.Full.CalculateHash();
            if (previous is not null && previous.Bytes.SequenceCompareTo(hash.Bytes) >= 0)
                throw new RlpException("Proof wrapper transactions must be in strictly ascending hash order.");
            previous = hash;
            transactions.Add(entry);
        }

        decoderContext.Check(txCheck);
        if (transactions.Count == 0) throw new RlpException("Proof wrapper must carry at least one transaction.");
        byte mode = decoderContext.DecodeByte();

        int contentCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
        int depsCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
        List<FrameDependency> deps = [];
        while (decoderContext.Position < depsCheck)
        {
            if (deps.Count == MaxWrapperDependencies) throw new RlpException("Proof wrapper exceeds the dependency count limit.");
            byte[] triple = decoderContext.DecodeByteArray(RlpLimit.For<MempoolWrapper>(Eip8288Constants.DependencyTripleLength, nameof(MempoolWrapper.Deps)));
            if (triple.Length != Eip8288Constants.DependencyTripleLength || !triple.AsSpan(0, 31).IsZero()
                || triple[31] is not (Eip8288Constants.LeanSphincsScheme or Eip8288Constants.LeanStarkScheme))
                throw new RlpException("Invalid dependency encoding.");
            deps.AddRange(Eip8288Dependencies.Parse(triple));
        }
        decoderContext.Check(depsCheck);

        List<byte[]>? proofs = null;
        RecursiveStark? recursiveStark = null;
        if (mode == MempoolWrapper.ModeDirect)
        {
            int proofsCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
            proofs = [];
            while (decoderContext.Position < proofsCheck)
            {
                if (proofs.Count == MaxWrapperDependencies)
                    throw new RlpException("Proof wrapper exceeds the witness count limit.");
                proofs.Add(decoderContext.DecodeByteArray(RlpLimit.For<RecursiveStark>(Eip8288Constants.MaxProofBytes, nameof(MempoolWrapper.Proofs))));
            }
            decoderContext.Check(proofsCheck);
        }
        else if (mode == MempoolWrapper.ModeRecursive)
        {
            byte[] starkProof = decoderContext.DecodeByteArray(RlpLimit.For<RecursiveStark>(Eip8288Constants.MaxProofBytes, nameof(RecursiveStark.StarkProof)));
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
        (int txContentLength, byte[][] txEntries) = GetTransactionsContent(item);
        byte[] depsBytes = Eip8288Dependencies.Serialize(item.Deps);
        (int contentContentLength, int innerContentLength) = GetContentLengths(item);

        writer.StartSequence(Rlp.LengthOfSequence(txContentLength) + Rlp.LengthOf((ulong)item.Mode) + Rlp.LengthOfSequence(contentContentLength));

        writer.StartSequence(txContentLength);
        for (int i = 0; i < txEntries.Length; i++)
        {
            WrapperTransaction transaction = item.Transactions[i];
            writer.StartSequence(EntryContentLength(txEntries[i]));
            writer.Encode((ulong)(transaction.IsHashOnly ? HashEntry : FullEntry));
            writer.Encode(txEntries[i]);
        }

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
        int txContentLength = 0;
        foreach (WrapperTransaction transaction in item.Transactions)
        {
            int entryLength = transaction.IsHashOnly
                ? Rlp.LengthOf(transaction.Hash)
                : Rlp.LengthOfByteString(TxDecoder.Instance.GetLength(transaction.Full!, RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping), 0);
            txContentLength += Rlp.LengthOfSequence(1 + entryLength);
        }
        (int contentContentLength, _) = GetContentLengths(item);

        return Rlp.LengthOfSequence(
            Rlp.LengthOfSequence(txContentLength)
            + Rlp.LengthOf((ulong)item.Mode)
            + Rlp.LengthOfSequence(contentContentLength));
    }

    private static (int ContentLength, byte[][] Entries) GetTransactionsContent(MempoolWrapper item)
    {
        byte[][] entries = new byte[item.Transactions.Count][];
        int contentLength = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            WrapperTransaction tx = item.Transactions[i];
            entries[i] = tx.IsHashOnly ? tx.Hash!.Bytes.ToArray() : Rlp.Encode(tx.Full!, RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping).Bytes;
            contentLength += Rlp.LengthOfSequence(EntryContentLength(entries[i]));
        }

        return (contentLength, entries);
    }

    /// <remarks>Both tags encode as one byte.</remarks>
    private static int EntryContentLength(byte[] entry) => 1 + Rlp.LengthOf(entry);

    private static (int ContentContentLength, int InnerContentLength) GetContentLengths(MempoolWrapper item)
    {
        int depsLength = Rlp.LengthOfSequence(item.Deps.Count * Rlp.LengthOfByteString(Eip8288Constants.DependencyTripleLength, 0));
        if (item.Mode == MempoolWrapper.ModeDirect)
        {
            int innerContentLength = 0;
            foreach (byte[] proof in item.Proofs ?? []) innerContentLength += Rlp.LengthOf(proof);
            return (depsLength + Rlp.LengthOfSequence(innerContentLength), innerContentLength);
        }

        return (depsLength + Rlp.LengthOf(item.RecursiveStark!.StarkProof), 0);
    }
}
