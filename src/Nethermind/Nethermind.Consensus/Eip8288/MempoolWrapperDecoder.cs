// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
                : new WrapperTransaction(Rlp.Decode<Transaction>(entry)));
        }

        byte mode = decoderContext.DecodeByte();

        decoderContext.ReadSequenceLength();
        List<FrameDependency> deps = Eip8288Dependencies.Parse(decoderContext.DecodeByteArray());

        List<byte[]>? proofs = null;
        RecursiveStark? recursiveStark = null;
        if (mode == MempoolWrapper.ModeDirect)
        {
            int proofsCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
            proofs = [];
            while (decoderContext.Position < proofsCheck)
            {
                proofs.Add(decoderContext.DecodeByteArray());
            }
        }
        else
        {
            decoderContext.ReadSequenceLength();
            byte[] starkProof = decoderContext.DecodeByteArray();
            Hash256 depsHash = decoderContext.DecodeKeccak()!;
            recursiveStark = new RecursiveStark(starkProof, depsHash);
        }

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
            entries[i] = tx.IsHashOnly ? tx.Hash!.Bytes.ToArray() : Rlp.Encode(tx.Full!).Bytes;
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
}
