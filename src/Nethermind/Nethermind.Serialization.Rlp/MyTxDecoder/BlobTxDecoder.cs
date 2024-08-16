// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp.Eip2930;

namespace Nethermind.Serialization.Rlp.MyTxDecoder;

public sealed class BlobTxDecoder(Func<Transaction>? transactionFactory = null) : ITxDecoder
{
    private static readonly AccessListDecoder AccessListDecoder = new();
    private readonly Func<Transaction> _createTransaction = transactionFactory ?? (() => new Transaction());

    public Transaction? Decode(Span<byte> transactionSequence, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        Transaction transaction = _createTransaction();
        transaction.Type = TxType.Blob;

        int positionAfterNetworkWrapper = 0;
        if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm))
        {
            int networkWrapperLength = rlpStream.ReadSequenceLength();
            positionAfterNetworkWrapper = rlpStream.Position + networkWrapperLength;
            int rlpLength = rlpStream.PeekNextRlpLength();
            transactionSequence = rlpStream.Peek(rlpLength);
        }

        int transactionLength = rlpStream.ReadSequenceLength();
        int lastCheck = rlpStream.Position + transactionLength;

        DecodePayload(transaction, rlpStream, rlpBehaviors);

        if (rlpStream.Position < lastCheck)
        {
            DecodeSignature(transaction, rlpStream, rlpBehaviors);
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
        {
            rlpStream.Check(lastCheck);
        }

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
            if (transactionSequence.Length <= ITxDecoder.MaxDelayedHashTxnSize)
            {
                // Delay hash generation, as may be filtered as having too low gas etc
                transaction.SetPreHashNoLock(transactionSequence);
            }
            else
            {
                // Just calculate the Hash as txn too large
                transaction.Hash = Keccak.Compute(transactionSequence);
            }
        }

        return transaction;
    }

    public void Decode(ref Transaction? transaction, int txSequenceStart, ReadOnlySpan<byte> transactionSequence, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        transaction ??= _createTransaction();
        transaction.Type = TxType.Blob;

        int networkWrapperCheck = 0;
        if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm))
        {
            int networkWrapperLength = decoderContext.ReadSequenceLength();
            networkWrapperCheck = decoderContext.Position + networkWrapperLength;
            int rlpRength = decoderContext.PeekNextRlpLength();
            txSequenceStart = decoderContext.Position;
            transactionSequence = decoderContext.Peek(rlpRength);
        }

        int transactionLength = decoderContext.ReadSequenceLength();
        int lastCheck = decoderContext.Position + transactionLength;

        DecodePayload(transaction, ref decoderContext, rlpBehaviors);

        if (decoderContext.Position < lastCheck)
        {
            DecodeSignature(transaction, ref decoderContext, rlpBehaviors);
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
        {
            decoderContext.Check(lastCheck);
        }

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
            if (transactionSequence.Length <= ITxDecoder.MaxDelayedHashTxnSize)
            {
                // Delay hash generation, as may be filtered as having too low gas etc
                if (decoderContext.ShouldSliceMemory)
                {
                    // Do not copy the memory in this case.
                    int currentPosition = decoderContext.Position;
                    decoderContext.Position = txSequenceStart;
                    transaction.SetPreHashMemoryNoLock(decoderContext.ReadMemory(transactionSequence.Length));
                    decoderContext.Position = currentPosition;
                }
                else
                {
                    transaction.SetPreHashNoLock(transactionSequence);
                }
            }
            else
            {
                // Just calculate the Hash immediately as txn too large
                transaction.Hash = Keccak.Compute(transactionSequence);
            }
        }
    }

    public void Encode(Transaction transaction, RlpStream stream, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        int contentLength = GetContentLength(transaction, forSigning, rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm));
        int sequenceLength = Rlp.LengthOfSequence(contentLength);

        if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == 0)
        {
            stream.StartByteArray(sequenceLength + 1, false);
        }

        stream.WriteByte((byte)TxType.Blob);

        if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm))
        {
            stream.StartSequence(contentLength);
            contentLength = GetContentLength(transaction, forSigning, false);
        }

        stream.StartSequence(contentLength);

        EncodePayload(transaction, stream, rlpBehaviors);
        EncodeSignature(transaction, stream, forSigning);

        if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm))
        {
            EncodeShardBlobNetworkWrapper(transaction, stream);
        }
    }

    public int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        int txContentLength = GetContentLength(transaction, forSigning, withNetworkWrapper: rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm));
        int txPayloadLength = Rlp.LengthOfSequence(txContentLength);

        bool isForTxRoot = rlpBehaviors.HasFlag(RlpBehaviors.SkipTypedWrapping);
        int result = isForTxRoot
                ? (1 + txPayloadLength)
                : Rlp.LengthOfSequence(1 + txPayloadLength);
        return result;
    }

    private static void DecodePayload(Transaction transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        transaction.ChainId = rlpStream.DecodeULong();
        transaction.Nonce = rlpStream.DecodeUInt256();
        transaction.GasPrice = rlpStream.DecodeUInt256(); // gas premium
        transaction.DecodedMaxFeePerGas = rlpStream.DecodeUInt256();
        transaction.GasLimit = rlpStream.DecodeLong();
        transaction.To = rlpStream.DecodeAddress();
        transaction.Value = rlpStream.DecodeUInt256();
        transaction.Data = rlpStream.DecodeByteArray();
        transaction.AccessList = AccessListDecoder.Decode(rlpStream, rlpBehaviors);
        transaction.MaxFeePerBlobGas = rlpStream.DecodeUInt256();
        transaction.BlobVersionedHashes = rlpStream.DecodeByteArrays();
    }

    private static void DecodePayload(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
    {
        transaction.ChainId = decoderContext.DecodeULong();
        transaction.Nonce = decoderContext.DecodeUInt256();
        transaction.GasPrice = decoderContext.DecodeUInt256(); // gas premium
        transaction.DecodedMaxFeePerGas = decoderContext.DecodeUInt256();
        transaction.GasLimit = decoderContext.DecodeLong();
        transaction.To = decoderContext.DecodeAddress();
        transaction.Value = decoderContext.DecodeUInt256();
        transaction.Data = decoderContext.DecodeByteArray();
        transaction.AccessList = AccessListDecoder.Decode(ref decoderContext, rlpBehaviors);
        transaction.MaxFeePerBlobGas = decoderContext.DecodeUInt256();
        transaction.BlobVersionedHashes = decoderContext.DecodeByteArrays();
    }

    private static void DecodeSignature(Transaction transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        ulong v = rlpStream.DecodeULong();
        ReadOnlySpan<byte> rBytes = rlpStream.DecodeByteArraySpan();
        ReadOnlySpan<byte> sBytes = rlpStream.DecodeByteArraySpan();
        transaction.Signature = SignatureBuilder.FromBytes(v + Signature.VOffset, rBytes, sBytes, rlpBehaviors) ?? transaction.Signature;
    }

    private static void DecodeSignature(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
    {
        ulong v = decoderContext.DecodeULong();
        ReadOnlySpan<byte> rBytes = decoderContext.DecodeByteArraySpan();
        ReadOnlySpan<byte> sBytes = decoderContext.DecodeByteArraySpan();
        transaction.Signature = SignatureBuilder.FromBytes(v + Signature.VOffset, rBytes, sBytes, rlpBehaviors) ?? transaction.Signature;
    }

    private static void DecodeShardBlobNetworkWrapper(Transaction transaction, RlpStream rlpStream)
    {
        byte[][] blobs = rlpStream.DecodeByteArrays();
        byte[][] commitments = rlpStream.DecodeByteArrays();
        byte[][] proofs = rlpStream.DecodeByteArrays();
        transaction.NetworkWrapper = new ShardBlobNetworkWrapper(blobs, commitments, proofs);
    }

    private static void DecodeShardBlobNetworkWrapper(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext)
    {
        byte[][] blobs = decoderContext.DecodeByteArrays();
        byte[][] commitments = decoderContext.DecodeByteArrays();
        byte[][] proofs = decoderContext.DecodeByteArrays();
        transaction.NetworkWrapper = new ShardBlobNetworkWrapper(blobs, commitments, proofs);
    }

    private static Hash256 CalculateHashForNetworkPayloadForm(ReadOnlySpan<byte> transactionSequence)
    {
        KeccakHash hash = KeccakHash.Create();
        Span<byte> txType = [(byte)TxType.Blob];
        hash.Update(txType);
        hash.Update(transactionSequence);
        return new Hash256(hash.Hash);
    }

    private static void EncodePayload(Transaction transaction, RlpStream stream, RlpBehaviors rlpBehaviors)
    {
        stream.Encode(transaction.ChainId ?? 0);
        stream.Encode(transaction.Nonce);
        stream.Encode(transaction.GasPrice); // gas premium
        stream.Encode(transaction.DecodedMaxFeePerGas);
        stream.Encode(transaction.GasLimit);
        stream.Encode(transaction.To);
        stream.Encode(transaction.Value);
        stream.Encode(transaction.Data);
        AccessListDecoder.Encode(stream, transaction.AccessList, rlpBehaviors);
        stream.Encode(transaction.MaxFeePerBlobGas.Value);
        stream.Encode(transaction.BlobVersionedHashes);
    }

    private static void EncodeSignature(Transaction transaction, RlpStream stream, bool forSigning)
    {
        if (!forSigning)
        {
            if (transaction.Signature is null)
            {
                stream.Encode(0);
                stream.Encode(Bytes.Empty);
                stream.Encode(Bytes.Empty);
            }
            else
            {
                stream.Encode(transaction.Signature.RecoveryId);
                stream.Encode(transaction.Signature.RAsSpan.WithoutLeadingZeros());
                stream.Encode(transaction.Signature.SAsSpan.WithoutLeadingZeros());
            }
        }
    }

    private static void EncodeShardBlobNetworkWrapper(Transaction transaction, RlpStream rlpStream)
    {
        ShardBlobNetworkWrapper networkWrapper = transaction.NetworkWrapper as ShardBlobNetworkWrapper;
        rlpStream.Encode(networkWrapper.Blobs);
        rlpStream.Encode(networkWrapper.Commitments);
        rlpStream.Encode(networkWrapper.Proofs);
    }

    private static int GetContentLength(Transaction transaction, bool forSigning, bool withNetworkWrapper = false)
    {
        int contentLength = GetPayloadLength(transaction) + GetSignatureLength(transaction, forSigning);

        return withNetworkWrapper
            ? GetShardBlobNetworkWrapperLength(transaction, contentLength)
            : contentLength;
    }

    private static int GetPayloadLength(Transaction transaction)
    {
        return Rlp.LengthOf(transaction.Nonce)
               + Rlp.LengthOf(transaction.GasPrice) // gas premium
               + Rlp.LengthOf(transaction.DecodedMaxFeePerGas)
               + Rlp.LengthOf(transaction.GasLimit)
               + Rlp.LengthOf(transaction.To)
               + Rlp.LengthOf(transaction.Value)
               + Rlp.LengthOf(transaction.Data)
               + Rlp.LengthOf(transaction.ChainId ?? 0)
               + AccessListDecoder.GetLength(transaction.AccessList, RlpBehaviors.None)
               + Rlp.LengthOf(transaction.MaxFeePerBlobGas)
               + Rlp.LengthOf(transaction.BlobVersionedHashes);
    }

    private static int GetSignatureLength(Transaction transaction, bool forSigning)
    {
        int contentLength = 0;

        if (!forSigning)
        {
            if (transaction.Signature is null)
            {
                contentLength += 1;
                contentLength += 1;
                contentLength += 1;
            }
            else
            {
                contentLength += Rlp.LengthOf(transaction.Signature.RecoveryId);
                contentLength += Rlp.LengthOf(transaction.Signature.RAsSpan.WithoutLeadingZeros());
                contentLength += Rlp.LengthOf(transaction.Signature.SAsSpan.WithoutLeadingZeros());
            }
        }

        return contentLength;
    }

    private static int GetShardBlobNetworkWrapperLength(Transaction transaction, int txContentLength)
    {
        ShardBlobNetworkWrapper networkWrapper = transaction.NetworkWrapper as ShardBlobNetworkWrapper;
        return Rlp.LengthOfSequence(txContentLength)
               + Rlp.LengthOf(networkWrapper.Blobs)
               + Rlp.LengthOf(networkWrapper.Commitments)
               + Rlp.LengthOf(networkWrapper.Proofs);
    }
}
