// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public sealed class BlobTxDecoder<T>(Func<T>? transactionFactory = null)
    : BaseEIP1559TxDecoder<T>(TxType.Blob, transactionFactory) where T : Transaction, new()
{
    public override Transaction? Decode(Span<byte> transactionSequence, RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int positionAfterNetworkWrapper = 0;
        if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm))
        {
            int networkWrapperLength = rlpStream.ReadSequenceLength();
            positionAfterNetworkWrapper = rlpStream.Position + networkWrapperLength;
            int rlpLength = rlpStream.PeekNextRlpLength();
            transactionSequence = rlpStream.Peek(rlpLength);
        }

        Transaction? transaction = base.Decode(transactionSequence, rlpStream, rlpBehaviors | RlpBehaviors.ExcludeHashes);

        if (transaction is not null)
        {
            if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm))
            {
                DecodeShardBlobNetworkWrapper(transaction, rlpStream);

                if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
                {
                    rlpStream.Check(positionAfterNetworkWrapper);
                }

                if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
                {
                    transaction.Hash = CalculateHashForNetworkPayloadForm(transactionSequence);
                }
            }
            else if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
            {
                CalculateHash(transaction!, transactionSequence);
            }
        }

        return transaction;
    }

    public override void Decode(ref Transaction? transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence,
        ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int networkWrapperCheck = 0;
        if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm))
        {
            int networkWrapperLength = decoderContext.ReadSequenceLength();
            networkWrapperCheck = decoderContext.Position + networkWrapperLength;
            int rlpRength = decoderContext.PeekNextRlpLength();
            txSequenceStart = decoderContext.Position;
            transactionSequence = decoderContext.Peek(rlpRength);
        }

        base.Decode(ref transaction, txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors | RlpBehaviors.ExcludeHashes);

        if (transaction is not null)
        {
            if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm))
            {
                DecodeShardBlobNetworkWrapper(transaction, ref decoderContext);

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

    protected override void EncodeTypedWrapped(Transaction transaction, RlpStream stream, RlpBehaviors rlpBehaviors, bool forSigning, int contentLength)
    {
        if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm))
        {
            stream.StartSequence(contentLength);
            // if the transaction is in mempool form, we started the mempool form sequence
            // and now we want to encode the non-mempool form contents, so we need to adjust the content length for that encoding
            contentLength = GetContentLength(transaction, rlpBehaviors & ~RlpBehaviors.InMempoolForm, forSigning);
        }

        // this always encodes in non-mempool form
        base.EncodeTypedWrapped(transaction, stream, rlpBehaviors, forSigning, contentLength);

        // we encode additional mempool form contents if needed
        if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm))
        {
            EncodeShardBlobNetworkWrapper(transaction, stream);
        }

        static void EncodeShardBlobNetworkWrapper(Transaction transaction, RlpStream rlpStream)
        {
            ShardBlobNetworkWrapper networkWrapper = (ShardBlobNetworkWrapper)transaction.NetworkWrapper!;
            if (networkWrapper.Version > ProofVersion.V0)
            {
                rlpStream.Encode((byte)networkWrapper.Version);
            }

            rlpStream.Encode(networkWrapper.Blobs);
            rlpStream.Encode(networkWrapper.Commitments);
            rlpStream.Encode(networkWrapper.Proofs);
        }
    }

    protected override void DecodePayload(Transaction transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        base.DecodePayload(transaction, rlpStream, rlpBehaviors);
        transaction.MaxFeePerBlobGas = rlpStream.DecodeUInt256();
        transaction.BlobVersionedHashes = rlpStream.DecodeByteArrays();
    }

    protected override void DecodePayload(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        base.DecodePayload(transaction, ref decoderContext, rlpBehaviors);
        transaction.MaxFeePerBlobGas = decoderContext.DecodeUInt256();
        transaction.BlobVersionedHashes = decoderContext.DecodeByteArrays();
    }

    protected override void EncodePayload(Transaction transaction, RlpStream stream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        base.EncodePayload(transaction, stream, rlpBehaviors);
        stream.Encode(transaction.MaxFeePerBlobGas!.Value);
        stream.Encode(transaction.BlobVersionedHashes!);
    }

    private static void DecodeShardBlobNetworkWrapper(Transaction transaction, RlpStream rlpStream)
    {
        ProofVersion version = ProofVersion.V0;
        var startingRlp = rlpStream.PeekNextItem();
        if (startingRlp.Length is 1)
        {
            version = (ProofVersion)rlpStream.ReadByte();
            if (version > ProofVersion.V1)
            {
                throw new RlpException($"Unknown version of {nameof(ShardBlobNetworkWrapper)}. Expected {0x01} and is {version}");
            }
        }

        byte[][] blobs = rlpStream.DecodeByteArrays();
        byte[][] commitments = rlpStream.DecodeByteArrays();
        byte[][] proofs = rlpStream.DecodeByteArrays();

        transaction.NetworkWrapper = new ShardBlobNetworkWrapper(blobs, commitments, proofs, version);
    }

    private static void DecodeShardBlobNetworkWrapper(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext)
    {
        ProofVersion version = ProofVersion.V0;
        var startingRlp = decoderContext.PeekNextItem();
        if (startingRlp.Length is 1)
        {
            version = (ProofVersion)decoderContext.ReadByte();
            if (version != ProofVersion.V1)
            {
                throw new RlpException($"Unknown version of {nameof(ShardBlobNetworkWrapper)}. Expected no more than {(int)ProofVersion.V1} and is {version}");
            }
        }

        byte[][] blobs = decoderContext.DecodeByteArrays();
        byte[][] commitments = decoderContext.DecodeByteArrays();
        byte[][] proofs = decoderContext.DecodeByteArrays();

        transaction.NetworkWrapper = new ShardBlobNetworkWrapper(blobs, commitments, proofs, version);
    }

    private static Hash256 CalculateHashForNetworkPayloadForm(ReadOnlySpan<byte> transactionSequence)
    {
        KeccakHash hash = KeccakHash.Create();
        Span<byte> txType = [(byte)TxType.Blob];
        hash.Update(txType);
        hash.Update(transactionSequence);
        return new Hash256(hash.Hash);
    }

    protected override int GetContentLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning,
        bool isEip155Enabled = false, ulong chainId = 0)
    {
        int contentLength = base.GetContentLength(transaction, rlpBehaviors, forSigning, isEip155Enabled, chainId);
        return rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm)
            ? GetShardBlobNetworkWrapperLength(transaction, contentLength)
            : contentLength;

        static int GetShardBlobNetworkWrapperLength(Transaction transaction, int txContentLength)
        {
            ShardBlobNetworkWrapper networkWrapper = (ShardBlobNetworkWrapper)transaction.NetworkWrapper!;
            return Rlp.LengthOfSequence(txContentLength)
                   + networkWrapper.Version switch { ProofVersion.V0 => 0, ProofVersion.V1 => 1, _ => throw new RlpException($"Unknown version of {nameof(ShardBlobNetworkWrapper)}: {networkWrapper.Version}") }
                   + Rlp.LengthOf(networkWrapper.Blobs)
                   + Rlp.LengthOf(networkWrapper.Commitments)
                   + Rlp.LengthOf(networkWrapper.Proofs);
        }
    }

    protected override int GetPayloadLength(Transaction transaction) =>
        base.GetPayloadLength(transaction)
        + Rlp.LengthOf(transaction.MaxFeePerBlobGas)
        + Rlp.LengthOf(transaction.BlobVersionedHashes);
}
