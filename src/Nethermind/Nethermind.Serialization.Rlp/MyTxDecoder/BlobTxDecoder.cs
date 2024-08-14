// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp.Eip2930;

namespace Nethermind.Serialization.Rlp.MyTxDecoder;

public sealed class BlobTxDecoder(bool lazyHash = true) : ITxDecoder
{
    public const int MaxDelayedHashTxnSize = 32768;

    private readonly AccessListDecoder _accessListDecoder = new();

    public Transaction? Decode(Span<byte> transactionSequence, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        Transaction transaction = new()
        {
            Type = TxType.Blob
        };

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

        DecodeShardBlobPayloadWithoutSig(transaction, rlpStream, rlpBehaviors);

        if (rlpStream.Position < lastCheck)
        {
            DecodeSignature(rlpStream, rlpBehaviors, transaction);
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
        {
            rlpStream.Check(lastCheck);
        }

        if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm) && transaction.MayHaveNetworkForm)
        {
            DecodeShardBlobNetworkPayload(transaction, rlpStream, rlpBehaviors);

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
            {
                rlpStream.Check(positionAfterNetworkWrapper);
            }

            if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
            {
                transaction.Hash = CalculateHashForNetworkPayloadForm(transaction.Type, transactionSequence);
            }
        }
        else if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
        {
            if (lazyHash && transactionSequence.Length <= MaxDelayedHashTxnSize)
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
        transaction ??= new();
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

        DecodeShardBlobPayloadWithoutSig(transaction, ref decoderContext, rlpBehaviors);

        if (decoderContext.Position < lastCheck)
        {
            DecodeSignature(ref decoderContext, rlpBehaviors, transaction);
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
        {
            decoderContext.Check(lastCheck);
        }

        if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm))
        {
            DecodeShardBlobNetworkPayload(transaction, ref decoderContext, rlpBehaviors);

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
            {
                decoderContext.Check(networkWrapperCheck);
            }

            if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
            {
                transaction.Hash = CalculateHashForNetworkPayloadForm(transaction.Type, transactionSequence);
            }
        }
        else if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
        {
            if (lazyHash && transactionSequence.Length <= TxDecoder.MaxDelayedHashTxnSize)
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

    public void EncodeTx(Transaction? item, RlpStream stream, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        int contentLength = GetContentLength(item, forSigning, rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm));
        int sequenceLength = Rlp.LengthOfSequence(contentLength);

        if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == 0)
        {
            stream.StartByteArray(sequenceLength + 1, false);
        }

        stream.WriteByte((byte)item.Type);

        if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm))
        {
            stream.StartSequence(contentLength);
            contentLength = GetContentLength(item, forSigning, false);
        }

        stream.StartSequence(contentLength);

        EncodeShardBlobPayloadWithoutPayload(item, stream, rlpBehaviors);
        EncodeSignature(stream, item, forSigning);

        if (rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm))
        {
            EncodeShardBlobNetworkPayload(item, stream);
        }
    }

    public int GetTxLength(Transaction tx, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        int txContentLength = GetContentLength(tx, forSigning, withNetworkWrapper: rlpBehaviors.HasFlag(RlpBehaviors.InMempoolForm));
        int txPayloadLength = Rlp.LengthOfSequence(txContentLength);

        bool isForTxRoot = rlpBehaviors.HasFlag(RlpBehaviors.SkipTypedWrapping);
        int result = isForTxRoot
                ? (1 + txPayloadLength)
                : Rlp.LengthOfSequence(1 + txPayloadLength);
        return result;
    }

    private void DecodeShardBlobPayloadWithoutSig(Transaction transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        transaction.ChainId = rlpStream.DecodeULong();
        transaction.Nonce = rlpStream.DecodeUInt256();
        transaction.GasPrice = rlpStream.DecodeUInt256(); // gas premium
        transaction.DecodedMaxFeePerGas = rlpStream.DecodeUInt256();
        transaction.GasLimit = rlpStream.DecodeLong();
        transaction.To = rlpStream.DecodeAddress();
        transaction.Value = rlpStream.DecodeUInt256();
        transaction.Data = rlpStream.DecodeByteArray();
        transaction.AccessList = _accessListDecoder.Decode(rlpStream, rlpBehaviors);
        transaction.MaxFeePerBlobGas = rlpStream.DecodeUInt256();
        transaction.BlobVersionedHashes = rlpStream.DecodeByteArrays();
    }

    private void DecodeShardBlobPayloadWithoutSig(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
    {
        transaction.ChainId = decoderContext.DecodeULong();
        transaction.Nonce = decoderContext.DecodeUInt256();
        transaction.GasPrice = decoderContext.DecodeUInt256(); // gas premium
        transaction.DecodedMaxFeePerGas = decoderContext.DecodeUInt256();
        transaction.GasLimit = decoderContext.DecodeLong();
        transaction.To = decoderContext.DecodeAddress();
        transaction.Value = decoderContext.DecodeUInt256();
        transaction.Data = decoderContext.DecodeByteArray();
        transaction.AccessList = _accessListDecoder.Decode(ref decoderContext, rlpBehaviors);
        transaction.MaxFeePerBlobGas = decoderContext.DecodeUInt256();
        transaction.BlobVersionedHashes = decoderContext.DecodeByteArrays();
    }

    private static void DecodeSignature(RlpStream rlpStream, RlpBehaviors rlpBehaviors, Transaction transaction)
    {
        ulong v = rlpStream.DecodeULong();
        ReadOnlySpan<byte> rBytes = rlpStream.DecodeByteArraySpan();
        ReadOnlySpan<byte> sBytes = rlpStream.DecodeByteArraySpan();
        ApplySignature(transaction, v, rBytes, sBytes, rlpBehaviors);
    }

    private static void DecodeSignature(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors, Transaction transaction)
    {
        ulong v = decoderContext.DecodeULong();
        ReadOnlySpan<byte> rBytes = decoderContext.DecodeByteArraySpan();
        ReadOnlySpan<byte> sBytes = decoderContext.DecodeByteArraySpan();
        ApplySignature(transaction, v, rBytes, sBytes, rlpBehaviors);
    }

    private static void ApplySignature(Transaction transaction, ulong v, ReadOnlySpan<byte> rBytes, ReadOnlySpan<byte> sBytes, RlpBehaviors rlpBehaviors)
    {
        if (transaction.Type == TxType.DepositTx && v == 0 && rBytes.IsEmpty && sBytes.IsEmpty) return;

        bool allowUnsigned = rlpBehaviors.HasFlag(RlpBehaviors.AllowUnsigned);
        bool isSignatureOk = true;
        string signatureError = null;
        if (rBytes.Length == 0 || sBytes.Length == 0)
        {
            isSignatureOk = false;
            signatureError = "VRS is 0 length when decoding Transaction";
        }
        else if (rBytes[0] == 0 || sBytes[0] == 0)
        {
            isSignatureOk = false;
            signatureError = "VRS starting with 0";
        }
        else if (rBytes.Length > 32 || sBytes.Length > 32)
        {
            isSignatureOk = false;
            signatureError = "R and S lengths expected to be less or equal 32";
        }
        else if (rBytes.SequenceEqual(Bytes.Zero32) && sBytes.SequenceEqual(Bytes.Zero32))
        {
            isSignatureOk = false;
            signatureError = "Both 'r' and 's' are zero when decoding a transaction.";
        }

        if (!isSignatureOk)
        {
            if (!allowUnsigned)
            {
                throw new RlpException(signatureError);
            }
        }
        else
        {
            Signature signature = new(rBytes, sBytes, v + Signature.VOffset);
            transaction.Signature = signature;
        }
    }

    private static void DecodeShardBlobNetworkPayload(Transaction transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        byte[][] blobs = rlpStream.DecodeByteArrays();
        byte[][] commitments = rlpStream.DecodeByteArrays();
        byte[][] proofs = rlpStream.DecodeByteArrays();
        transaction.NetworkWrapper = new ShardBlobNetworkWrapper(blobs, commitments, proofs);
    }

    private static void DecodeShardBlobNetworkPayload(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
    {
        byte[][] blobs = decoderContext.DecodeByteArrays();
        byte[][] commitments = decoderContext.DecodeByteArrays();
        byte[][] proofs = decoderContext.DecodeByteArrays();
        transaction.NetworkWrapper = new ShardBlobNetworkWrapper(blobs, commitments, proofs);
    }

    private static Hash256 CalculateHashForNetworkPayloadForm(TxType type, ReadOnlySpan<byte> transactionSequence)
    {
        KeccakHash hash = KeccakHash.Create();
        Span<byte> txType = [(byte)type];
        hash.Update(txType);
        hash.Update(transactionSequence);
        return new Hash256(hash.Hash);
    }

    private void EncodeShardBlobPayloadWithoutPayload(Transaction item, RlpStream stream, RlpBehaviors rlpBehaviors)
    {
        stream.Encode(item.ChainId ?? 0);
        stream.Encode(item.Nonce);
        stream.Encode(item.GasPrice); // gas premium
        stream.Encode(item.DecodedMaxFeePerGas);
        stream.Encode(item.GasLimit);
        stream.Encode(item.To);
        stream.Encode(item.Value);
        stream.Encode(item.Data);
        _accessListDecoder.Encode(stream, item.AccessList, rlpBehaviors);
        stream.Encode(item.MaxFeePerBlobGas.Value);
        stream.Encode(item.BlobVersionedHashes);
    }

    private static void EncodeSignature(RlpStream stream, Transaction item, bool forSigning)
    {
        if (!forSigning)
        {
            if (item.Signature is null)
            {
                stream.Encode(0);
                stream.Encode(Bytes.Empty);
                stream.Encode(Bytes.Empty);
            }
            else
            {
                stream.Encode(item.Signature.RecoveryId);
                stream.Encode(item.Signature.RAsSpan.WithoutLeadingZeros());
                stream.Encode(item.Signature.SAsSpan.WithoutLeadingZeros());
            }
        }
    }

    private static void EncodeShardBlobNetworkPayload(Transaction transaction, RlpStream rlpStream)
    {
        ShardBlobNetworkWrapper networkWrapper = transaction.NetworkWrapper as ShardBlobNetworkWrapper;
        rlpStream.Encode(networkWrapper.Blobs);
        rlpStream.Encode(networkWrapper.Commitments);
        rlpStream.Encode(networkWrapper.Proofs);
    }

    private int GetContentLength(Transaction item, bool forSigning, bool withNetworkWrapper = false)
    {
        int contentLength = GetShardBlobContentLength(item);
        contentLength += GetSignatureContentLength(item, forSigning);

        if (withNetworkWrapper)
        {
            contentLength = GetShardBlobNetworkWrapperContentLength(item, contentLength);
        }
        return contentLength;
    }

    private int GetShardBlobContentLength(Transaction item)
    {
        return Rlp.LengthOf(item.Nonce)
               + Rlp.LengthOf(item.GasPrice) // gas premium
               + Rlp.LengthOf(item.DecodedMaxFeePerGas)
               + Rlp.LengthOf(item.GasLimit)
               + Rlp.LengthOf(item.To)
               + Rlp.LengthOf(item.Value)
               + Rlp.LengthOf(item.Data)
               + Rlp.LengthOf(item.ChainId ?? 0)
               + _accessListDecoder.GetLength(item.AccessList, RlpBehaviors.None)
               + Rlp.LengthOf(item.MaxFeePerBlobGas)
               + Rlp.LengthOf(item.BlobVersionedHashes);
    }

    private static int GetSignatureContentLength(Transaction item, bool forSigning)
    {
        int contentLength = 0;

        if (!forSigning)
        {
            if (item.Signature is null)
            {
                contentLength += 1;
                contentLength += 1;
                contentLength += 1;
            }
            else
            {
                contentLength += Rlp.LengthOf(item.Signature.RecoveryId);
                contentLength += Rlp.LengthOf(item.Signature.RAsSpan.WithoutLeadingZeros());
                contentLength += Rlp.LengthOf(item.Signature.SAsSpan.WithoutLeadingZeros());
            }
        }

        return contentLength;
    }

    private static int GetShardBlobNetworkWrapperContentLength(Transaction item, int txContentLength)
    {
        ShardBlobNetworkWrapper networkWrapper = item.NetworkWrapper as ShardBlobNetworkWrapper;
        return Rlp.LengthOfSequence(txContentLength)
               + Rlp.LengthOf(networkWrapper.Blobs)
               + Rlp.LengthOf(networkWrapper.Commitments)
               + Rlp.LengthOf(networkWrapper.Proofs);
    }
}
