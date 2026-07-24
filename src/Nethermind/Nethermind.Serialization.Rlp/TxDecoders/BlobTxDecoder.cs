// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using CkzgLib;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public sealed class BlobTxDecoder<T>(Func<T>? transactionFactory = null)
    : BaseEIP1559TxDecoder<T>(TxType.Blob, transactionFactory) where T : Transaction, new()
{
    private const int BlobCountLimit = 128;
    private const int BlobCellProofsCountLimit = BlobCountLimit * Ckzg.CellsPerExtBlob;

    public static readonly RlpLimit BlobVersionedHashesCountLimit = RlpLimit.For<Transaction>(BlobCountLimit, nameof(Transaction.BlobVersionedHashes));

    private static readonly RlpLimit NetworkWrapperBlobsCountLimit = RlpLimit.For<ShardBlobNetworkWrapper>(BlobCountLimit, nameof(ShardBlobNetworkWrapper.Blobs));
    private static readonly RlpLimit NetworkWrapperCommitmentsCountLimit = RlpLimit.For<ShardBlobNetworkWrapper>(BlobCountLimit, nameof(ShardBlobNetworkWrapper.Commitments));
    private static readonly RlpLimit NetworkWrapperProofsCountLimit = RlpLimit.For<ShardBlobNetworkWrapper>(BlobCountLimit, $"{nameof(ShardBlobNetworkWrapper.Proofs)} {ProofVersion.V0}");
    private static readonly RlpLimit NetworkWrapperCellProofsCountLimit = RlpLimit.For<ShardBlobNetworkWrapper>(BlobCellProofsCountLimit, $"{nameof(ShardBlobNetworkWrapper.Proofs)} {ProofVersion.V1}");

    public override void Decode(ref Transaction? transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence,
        ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int networkWrapperCheck = 0;
        if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm))
        {
            int networkWrapperLength = decoderContext.ReadSequenceLength();
            networkWrapperCheck = decoderContext.Position + networkWrapperLength;
            int rlpLength = decoderContext.PeekNextRlpLength();
            txSequenceStart = decoderContext.Position;
            transactionSequence = decoderContext.Peek(rlpLength);
        }

        base.Decode(ref transaction, txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors | RlpBehaviors.ExcludeHashes);

        if (transaction is not null)
        {
            if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm))
            {
                DecodeShardBlobNetworkWrapper(transaction, ref decoderContext, rlpBehaviors);

                if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
                {
                    decoderContext.Check(networkWrapperCheck);
                }

                if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
                {
                    transaction.Hash = CalculateHashForNetworkPayloadForm(transactionSequence);
                }
            }
            else if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
            {
                CalculateHash(transaction, txSequenceStart, transactionSequence, ref decoderContext);
            }
        }
    }

    protected override void EncodeTypedWrapped<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors, bool forSigning, int contentLength)
    {
        if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm))
        {
            writer.StartSequence(contentLength);
            // if the transaction is in mempool form, we started the mempool form sequence
            // and now we want to encode the non-mempool form contents, so we need to adjust the content length for that encoding
            contentLength = GetContentLength(transaction, rlpBehaviors & ~RlpBehaviors.InMempoolForm, forSigning);
        }

        // this always encodes in non-mempool form
        base.EncodeTypedWrapped(transaction, ref writer, rlpBehaviors, forSigning, contentLength);

        // we encode additional mempool form contents if needed
        if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm))
        {
            EncodeShardBlobNetworkWrapper(transaction, ref writer, rlpBehaviors);
        }

        static void EncodeShardBlobNetworkWrapper(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors)
        {
            ShardBlobNetworkWrapper networkWrapper = (ShardBlobNetworkWrapper)transaction.NetworkWrapper!;
            if (networkWrapper.Version > ProofVersion.V0)
            {
                writer.Encode((byte)networkWrapper.Version);
            }

            if (networkWrapper.Blobs.Length == 0 && !rlpBehaviors.HasFlag(RlpBehaviors.Storage))
            {
                writer.EncodeEmptyByteArray();
            }
            else
            {
                writer.Encode(networkWrapper.Blobs);
            }
            writer.Encode(networkWrapper.Commitments);
            writer.Encode(networkWrapper.Proofs);

            if (rlpBehaviors.HasFlag(RlpBehaviors.Storage))
            {
                Span<byte> cellMaskBytes = stackalloc byte[BlobCellMask.FixedByteLength];
                networkWrapper.CellMask.WriteTo(cellMaskBytes);
                writer.Encode(cellMaskBytes);
                writer.Encode(networkWrapper.Cells ?? []);
            }
        }
    }

    protected override void DecodePayload(Transaction transaction, ref RlpReader decoderContext,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        base.DecodePayload(transaction, ref decoderContext, rlpBehaviors);
        transaction.MaxFeePerBlobGas = decoderContext.DecodeUInt256();
        transaction.BlobVersionedHashes = decoderContext.DecodeByteArrays(BlobVersionedHashesCountLimit, innerSize: Hash256.Size);
    }

    protected override void EncodePayload<TWriter>(Transaction transaction, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        base.EncodePayload(transaction, ref writer, rlpBehaviors);
        writer.Encode(transaction.MaxFeePerBlobGas!.Value);
        writer.Encode(transaction.BlobVersionedHashes!);
    }

    private static void DecodeShardBlobNetworkWrapper(Transaction transaction, ref RlpReader decoderContext, RlpBehaviors rlpBehaviors)
    {
        ProofVersion version = ProofVersion.V0;
        if (!decoderContext.IsSequenceNext() && !decoderContext.IsNextItemEmptyByteArray())
        {
            version = (ProofVersion)decoderContext.ReadByte();
            if (version > ProofVersion.V1)
            {
                throw new RlpException($"Unknown version of {nameof(ShardBlobNetworkWrapper)}. Expected no more than {(int)ProofVersion.V1} and is {version}");
            }
        }

        byte[][] blobs;
        if (decoderContext.IsNextItemEmptyByteArray())
        {
            decoderContext.DecodeByteArraySpan();
            blobs = [];
        }
        else
        {
            blobs = decoderContext.DecodeByteArrays(NetworkWrapperBlobsCountLimit);
        }
        byte[][] commitments = decoderContext.DecodeByteArrays(NetworkWrapperCommitmentsCountLimit);
        RlpLimit proofsCountLimit = version is ProofVersion.V1 ? NetworkWrapperCellProofsCountLimit : NetworkWrapperProofsCountLimit;
        byte[][] proofs = decoderContext.DecodeByteArrays(proofsCountLimit);
        BlobCellMask cellMask = default;
        byte[][]? cells = null;

        if (rlpBehaviors.HasFlag(RlpBehaviors.Storage) && decoderContext.PeekNumberOfItemsRemaining(maxSearch: 2) > 0)
        {
            cellMask = BlobCellMask.FromBytes(decoderContext.DecodeByteArraySpan());
            byte[][] decodedCells = decoderContext.DecodeByteArrays(NetworkWrapperCellProofsCountLimit);
            cells = cellMask.IsEmpty && decodedCells.Length == 0 ? null : decodedCells;
        }

        transaction.NetworkWrapper = new ShardBlobNetworkWrapper(blobs, commitments, proofs, version, cellMask, cells);
    }

    private static Hash256 CalculateHashForNetworkPayloadForm(ReadOnlySpan<byte> transactionSequence)
    {
        KeccakHash hash = KeccakHash.Create();
        Span<byte> txType = [(byte)TxType.Blob];
        hash.Update(txType);
        hash.Update(transactionSequence);
        return new Hash256(hash.GenerateValueHash());
    }

    protected override int GetContentLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning,
        bool isEip155Enabled = false, ulong chainId = 0)
    {
        int contentLength = base.GetContentLength(transaction, rlpBehaviors, forSigning, isEip155Enabled, chainId);
        return rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm)
            ? GetShardBlobNetworkWrapperLength(transaction, contentLength)
            : contentLength;

        int GetShardBlobNetworkWrapperLength(Transaction transaction, int txContentLength)
        {
            ShardBlobNetworkWrapper networkWrapper = (ShardBlobNetworkWrapper)transaction.NetworkWrapper!;
            return Rlp.LengthOfSequence(txContentLength)
                   + networkWrapper.Version switch { ProofVersion.V0 => 0, ProofVersion.V1 => 1, _ => throw new RlpException($"Unknown version of {nameof(ShardBlobNetworkWrapper)}: {networkWrapper.Version}") }
                   + Rlp.LengthOf(networkWrapper.Blobs)
                   + Rlp.LengthOf(networkWrapper.Commitments)
                   + Rlp.LengthOf(networkWrapper.Proofs)
                   + (rlpBehaviors.HasFlag(RlpBehaviors.Storage)
                       ? Rlp.LengthOfByteString(BlobCellMask.FixedByteLength, firstByte: 0) + Rlp.LengthOf(networkWrapper.Cells ?? [])
                       : 0);
        }
    }

    protected override int GetPayloadLength(Transaction transaction) =>
        base.GetPayloadLength(transaction)
        + Rlp.LengthOf(transaction.MaxFeePerBlobGas)
        + Rlp.LengthOf(transaction.BlobVersionedHashes);
}
