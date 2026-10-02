// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Eip8288;

/// <summary>
/// RLP codec for the EIP-8288 mempool wrapper <c>[transactions, mode, content]</c>. A transaction
/// entry is either a full transaction (its network encoding) or a 32-byte hash when already
/// broadcast; the content is <c>[deps, proofs]</c> for mode 0 or <c>[deps, [stark_proof, deps_hash]]</c>
/// for mode 1.
/// </summary>
public sealed class MempoolWrapperDecoder : RlpDecoder<MempoolWrapper>
{
    public static readonly MempoolWrapperDecoder Instance = new();

    protected override MempoolWrapper DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int length = decoderContext.ReadSequenceLength();
        int check = length + decoderContext.Position;

        int txCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
        List<WrapperTransaction> transactions = [];
        while (decoderContext.Position < txCheck)
        {
            // A 32-byte entry is an already-broadcast tx hash; anything else is a full tx (a real
            // transaction encoding is never exactly 32 bytes).
            byte[] entry = decoderContext.DecodeByteArray();
            transactions.Add(entry.Length == Hash256.Size
                ? new WrapperTransaction(new Hash256(entry))
                : new WrapperTransaction(TxDecoder.Instance.DecodeCompleteNotNull(entry, RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping)));
        }

        decoderContext.Check(txCheck);
        byte mode = decoderContext.DecodeByte();

        int contentCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
        // A trailing partial triple would decode to the same wrapper as the truncated blob, making the
        // encoding malleable; the transaction side enforces the same constraint.
        byte[] depsBlob = decoderContext.DecodeByteArray(RlpLimit.For<MempoolWrapper>((Eip8288Constants.MaxLeanSigDepsPerWrapper + Eip8288Constants.MaxLeanStarkDepsPerWrapper) * Eip8288Constants.DependencyTripleLength, nameof(MempoolWrapper.Deps)));
        if (depsBlob.Length % Eip8288Constants.DependencyTripleLength != 0)
        {
            throw new RlpException($"{nameof(MempoolWrapper)} deps length {depsBlob.Length} is not a multiple of {Eip8288Constants.DependencyTripleLength}");
        }

        for (int offset = 0; offset < depsBlob.Length; offset += Eip8288Constants.DependencyTripleLength)
        {
            ReadOnlySpan<byte> triple = depsBlob.AsSpan(offset, Eip8288Constants.DependencyTripleLength);
            if (!triple[..31].IsZero() || triple[31] is not (Eip8288Constants.LeanSphincsScheme or Eip8288Constants.LeanStarkScheme))
                throw new RlpException("Invalid dependency scheme encoding.");
        }
        List<FrameDependency> deps = Eip8288Dependencies.Parse(depsBlob);

        List<byte[]>? proofs = null;
        RecursiveStark? recursiveStark = null;
        if (mode == MempoolWrapper.ModeDirect)
        {
            int proofsCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
            proofs = [];
            while (decoderContext.Position < proofsCheck)
            {
                proofs.Add(decoderContext.DecodeByteArray(RlpLimit.For<RecursiveStark>(Eip8288Constants.MaxProofBytes, nameof(MempoolWrapper.Proofs))));
            }
            decoderContext.Check(proofsCheck);
        }
        else if (mode == MempoolWrapper.ModeRecursive)
        {
            int recursiveCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
            byte[] starkProof = decoderContext.DecodeByteArray(RlpLimit.For<RecursiveStark>(Eip8288Constants.MaxProofBytes, nameof(RecursiveStark.StarkProof)));
            Hash256 depsHash = decoderContext.DecodeKeccak() ?? ThrowMissingBlockDepsHash();
            decoderContext.Check(recursiveCheck);
            recursiveStark = new RecursiveStark(starkProof, depsHash);
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
        (int contentContentLength, int innerContentLength) = GetContentLengths(item, depsBytes);

        writer.StartSequence(Rlp.LengthOfSequence(txContentLength) + Rlp.LengthOf((ulong)item.Mode) + Rlp.LengthOfSequence(contentContentLength));

        writer.StartSequence(txContentLength);
        foreach (byte[] entry in txEntries) writer.Encode(entry);

        writer.Encode((ulong)item.Mode);

        writer.StartSequence(contentContentLength);
        writer.Encode(depsBytes);
        if (item.Mode == MempoolWrapper.ModeDirect)
        {
            writer.StartSequence(innerContentLength);
            foreach (byte[] proof in item.Proofs!) writer.Encode(proof);
        }
        else
        {
            writer.StartSequence(innerContentLength);
            writer.Encode(item.RecursiveStark!.StarkProof);
            writer.Encode(item.RecursiveStark!.BlockDepsHash);
        }
    }

    public override int GetLength(MempoolWrapper item, RlpBehaviors rlpBehaviors)
    {
        (int txContentLength, _) = GetTransactionsContent(item);
        byte[] depsBytes = Eip8288Dependencies.Serialize(item.Deps);
        (int contentContentLength, _) = GetContentLengths(item, depsBytes);

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
            entries[i] = tx.IsHashOnly ? tx.Hash!.Bytes.ToArray() : Rlp.Encode(tx.Full!, RlpBehaviors.InMempoolForm).Bytes;
            contentLength += Rlp.LengthOf(entries[i]);
        }

        return (contentLength, entries);
    }

    private static (int ContentContentLength, int InnerContentLength) GetContentLengths(MempoolWrapper item, byte[] depsBytes)
    {
        int innerContentLength;
        if (item.Mode == MempoolWrapper.ModeDirect)
        {
            innerContentLength = 0;
            foreach (byte[] proof in item.Proofs ?? []) innerContentLength += Rlp.LengthOf(proof);
        }
        else
        {
            innerContentLength = Rlp.LengthOf(item.RecursiveStark!.StarkProof) + Rlp.LengthOf(item.RecursiveStark!.BlockDepsHash);
        }

        return (Rlp.LengthOf(depsBytes) + Rlp.LengthOfSequence(innerContentLength), innerContentLength);
    }

    [DoesNotReturn]
    private static Hash256 ThrowMissingBlockDepsHash() =>
        throw new RlpException($"Missing {nameof(RecursiveStark.BlockDepsHash)} in {nameof(MempoolWrapper)}");
}
