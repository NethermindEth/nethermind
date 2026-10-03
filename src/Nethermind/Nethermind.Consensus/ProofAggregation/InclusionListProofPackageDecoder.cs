// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>
/// RLP codec for an inclusion-list proof package <c>[transactions, [stark_proof, deps_hash, proven_dependencies]]</c>.
/// </summary>
public sealed class InclusionListProofPackageDecoder : RlpDecoder<InclusionListProofPackage>
{
    public static readonly InclusionListProofPackageDecoder Instance = new();

    protected override InclusionListProofPackage DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int length = decoderContext.ReadSequenceLength();
        int check = length + decoderContext.Position;

        int txCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
        List<Transaction> transactions = [];
        int transactionBytes = 0;
        while (decoderContext.Position < txCheck)
        {
            if (transactions.Count >= Eip7805Constants.MaxAggregateInclusionListTransactions)
                throw new RlpException("Too many inclusion-list transactions");
            byte[] encoded = decoderContext.DecodeByteArray(RlpLimit.For<InclusionListProofPackage>(
                Eip7805Constants.MaxAggregateInclusionListBytes, nameof(InclusionListProofPackage.Transactions)));
            transactionBytes += encoded.Length;
            if (transactionBytes > Eip7805Constants.MaxAggregateInclusionListBytes)
                throw new RlpException("Inclusion-list transactions exceed the aggregate byte limit");
            transactions.Add(TxDecoder.Instance.DecodeCompleteNotNull(encoded, RlpBehaviors.SkipTypedWrapping));
        }

        decoderContext.Check(txCheck);
        int proofCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
        byte[] starkProof = decoderContext.DecodeByteArray(RlpLimit.For<RecursiveStark>(Eip8288Constants.MaxProofBytes, nameof(RecursiveStark.StarkProof)));
        Hash256 depsHash = decoderContext.DecodeKeccak() ?? ThrowMissingBlockDepsHash();
        byte[] provenDependencies = decoderContext.DecodeByteArray(RlpLimit.For<InclusionListProofPackage>(
            Eip8288Constants.MaxInclusionListDependencyBytes, nameof(InclusionListProofPackage.ProvenDependencies)));
        if (!InclusionListProofValidator.HasValidMetadataLength(provenDependencies))
            throw new RlpException("Invalid inclusion-list dependency metadata length");
        decoderContext.Check(proofCheck);

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            decoderContext.Check(check);
        }

        return new InclusionListProofPackage
        {
            Transactions = transactions,
            RecursiveStark = new RecursiveStark(starkProof, depsHash),
            ProvenDependencies = provenDependencies,
        };
    }

    public override void Encode<TWriter>(ref TWriter writer, InclusionListProofPackage item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        (int txContentLength, byte[][] txEntries) = GetTransactionsContent(item);
        int recursiveStarkContentLength = Rlp.LengthOf(item.RecursiveStark.StarkProof) + Rlp.LengthOf(item.RecursiveStark.BlockDepsHash)
            + Rlp.LengthOf(item.ProvenDependencies ?? []);

        writer.StartSequence(Rlp.LengthOfSequence(txContentLength) + Rlp.LengthOfSequence(recursiveStarkContentLength));

        writer.StartSequence(txContentLength);
        foreach (byte[] entry in txEntries) writer.Encode(entry);

        writer.StartSequence(recursiveStarkContentLength);
        writer.Encode(item.RecursiveStark.StarkProof);
        writer.Encode(item.RecursiveStark.BlockDepsHash);
        writer.Encode(item.ProvenDependencies ?? []);
    }

    public override int GetLength(InclusionListProofPackage item, RlpBehaviors rlpBehaviors)
    {
        (int txContentLength, _) = GetTransactionsContent(item);
        int recursiveStarkContentLength = Rlp.LengthOf(item.RecursiveStark.StarkProof) + Rlp.LengthOf(item.RecursiveStark.BlockDepsHash)
            + Rlp.LengthOf(item.ProvenDependencies ?? []);

        return Rlp.LengthOfSequence(Rlp.LengthOfSequence(txContentLength) + Rlp.LengthOfSequence(recursiveStarkContentLength));
    }

    private static (int ContentLength, byte[][] Entries) GetTransactionsContent(InclusionListProofPackage item)
    {
        byte[][] entries = new byte[item.Transactions.Count][];
        int contentLength = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            entries[i] = Rlp.Encode(item.Transactions[i], RlpBehaviors.SkipTypedWrapping).Bytes;
            contentLength += Rlp.LengthOf(entries[i]);
        }

        return (contentLength, entries);
    }

    [DoesNotReturn]
    private static Hash256 ThrowMissingBlockDepsHash() =>
        throw new RlpException($"Missing {nameof(RecursiveStark.BlockDepsHash)} in {nameof(InclusionListProofPackage)}");
}
