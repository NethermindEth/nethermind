// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Eip8288;

/// <summary>
/// RLP codec for the EIP-8288 FOCIL inclusion list <c>[transactions, [stark_proof, deps_hash]]</c>.
/// </summary>
public sealed class FocilInclusionListDecoder : RlpDecoder<FocilInclusionList>
{
    public static readonly FocilInclusionListDecoder Instance = new();

    protected override FocilInclusionList DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int length = decoderContext.ReadSequenceLength();
        int check = length + decoderContext.Position;

        int txCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
        List<Transaction> transactions = [];
        while (decoderContext.Position < txCheck)
        {
            transactions.Add(Rlp.Decode<Transaction>(decoderContext.DecodeByteArray()));
        }

        decoderContext.ReadSequenceLength();
        byte[] starkProof = decoderContext.DecodeByteArray();
        Hash256 depsHash = decoderContext.DecodeKeccak()!;

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            decoderContext.Check(check);
        }

        return new FocilInclusionList
        {
            Transactions = transactions,
            RecursiveStark = new RecursiveStark(starkProof, depsHash),
        };
    }

    public override void Encode<TWriter>(ref TWriter writer, FocilInclusionList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        (int txContentLength, byte[][] txEntries) = GetTransactionsContent(item);
        int recursiveStarkContentLength = Rlp.LengthOf(item.RecursiveStark.StarkProof) + Rlp.LengthOf(item.RecursiveStark.BlockDepsHash);

        writer.StartSequence(Rlp.LengthOfSequence(txContentLength) + Rlp.LengthOfSequence(recursiveStarkContentLength));

        writer.StartSequence(txContentLength);
        foreach (byte[] entry in txEntries) writer.Encode(entry);

        writer.StartSequence(recursiveStarkContentLength);
        writer.Encode(item.RecursiveStark.StarkProof);
        writer.Encode(item.RecursiveStark.BlockDepsHash);
    }

    public override int GetLength(FocilInclusionList item, RlpBehaviors rlpBehaviors)
    {
        (int txContentLength, _) = GetTransactionsContent(item);
        int recursiveStarkContentLength = Rlp.LengthOf(item.RecursiveStark.StarkProof) + Rlp.LengthOf(item.RecursiveStark.BlockDepsHash);

        return Rlp.LengthOfSequence(Rlp.LengthOfSequence(txContentLength) + Rlp.LengthOfSequence(recursiveStarkContentLength));
    }

    private static (int ContentLength, byte[][] Entries) GetTransactionsContent(FocilInclusionList item)
    {
        byte[][] entries = new byte[item.Transactions.Count][];
        int contentLength = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            entries[i] = Rlp.Encode(item.Transactions[i]).Bytes;
            contentLength += Rlp.LengthOf(entries[i]);
        }

        return (contentLength, entries);
    }
}
