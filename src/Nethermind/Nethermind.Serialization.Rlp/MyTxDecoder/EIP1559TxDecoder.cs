// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp.Eip2930;

namespace Nethermind.Serialization.Rlp.MyTxDecoder;

public sealed class EIP1559TxDecoder(bool lazyHash = true) : ITxDecoder
{
    public const int MaxDelayedHashTxnSize = 32768;

    private readonly AccessListDecoder _accessListDecoder = new();

    public Transaction? Decode(Span<byte> transactionSequence, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        Transaction transaction = new()
        {
            Type = TxType.EIP1559
        };

        int transactionLength = rlpStream.ReadSequenceLength();
        int lastCheck = rlpStream.Position + transactionLength;

        DecodeEip1559PayloadWithoutSig(transaction, rlpStream, rlpBehaviors);

        if (rlpStream.Position < lastCheck)
        {
            DecodeSignature(rlpStream, rlpBehaviors, transaction);
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
        {
            rlpStream.Check(lastCheck);
        }

        if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
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
        transaction.Type = TxType.EIP1559;

        int transactionLength = decoderContext.ReadSequenceLength();
        int lastCheck = decoderContext.Position + transactionLength;

        DecodeEip1559PayloadWithoutSig(transaction, ref decoderContext, rlpBehaviors);

        if (decoderContext.Position < lastCheck)
        {
            DecodeSignature(ref decoderContext, rlpBehaviors, transaction);
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
        {
            decoderContext.Check(lastCheck);
        }

        if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
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
        int contentLength = GetContentLength(item, forSigning);
        int sequenceLength = Rlp.LengthOfSequence(contentLength);

        if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == 0)
        {
            stream.StartByteArray(sequenceLength + 1, false);
        }

        stream.WriteByte((byte)item.Type);
        stream.StartSequence(contentLength);

        EncodeEip1559PayloadWithoutPayload(item, stream, rlpBehaviors);
        EncodeSignature(stream, item, forSigning);
    }

    public int GetLength(Transaction tx, RlpBehaviors rlpBehaviors)
    {
        int txContentLength = GetContentLength(tx, forSigning: false);
        int txPayloadLength = Rlp.LengthOfSequence(txContentLength);

        bool isForTxRoot = rlpBehaviors.HasFlag(RlpBehaviors.SkipTypedWrapping);
        int result = isForTxRoot
                ? (1 + txPayloadLength)
                : Rlp.LengthOfSequence(1 + txPayloadLength);
        return result;
    }

    public int GetTxLength(Transaction tx, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        int txContentLength = GetContentLength(tx, forSigning);
        int txPayloadLength = Rlp.LengthOfSequence(txContentLength);

        bool isForTxRoot = rlpBehaviors.HasFlag(RlpBehaviors.SkipTypedWrapping);
        int result = isForTxRoot
                ? (1 + txPayloadLength)
                : Rlp.LengthOfSequence(1 + txPayloadLength);
        return result;
    }

    private void DecodeEip1559PayloadWithoutSig(Transaction transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
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
    }

    private void DecodeEip1559PayloadWithoutSig(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
    {
        transaction.ChainId = decoderContext.DecodeULong();
        transaction.Nonce = decoderContext.DecodeUInt256();
        transaction.GasPrice = decoderContext.DecodeUInt256(); // gas premium
        transaction.DecodedMaxFeePerGas = decoderContext.DecodeUInt256();
        transaction.GasLimit = decoderContext.DecodeLong();
        transaction.To = decoderContext.DecodeAddress();
        transaction.Value = decoderContext.DecodeUInt256();
        transaction.Data = decoderContext.DecodeByteArrayMemory();
        transaction.AccessList = _accessListDecoder.Decode(ref decoderContext, rlpBehaviors);
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

    private void EncodeEip1559PayloadWithoutPayload(Transaction item, RlpStream stream, RlpBehaviors rlpBehaviors)
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

    private int GetContentLength(Transaction item, bool forSigning)
    {
        var contentLength = GetEip1559ContentLength(item);
        contentLength += GetSignatureContentLength(item, forSigning);

        return contentLength;
    }

    private int GetEip1559ContentLength(Transaction item)
    {
        return Rlp.LengthOf(item.Nonce)
               + Rlp.LengthOf(item.GasPrice) // gas premium
               + Rlp.LengthOf(item.DecodedMaxFeePerGas)
               + Rlp.LengthOf(item.GasLimit)
               + Rlp.LengthOf(item.To)
               + Rlp.LengthOf(item.Value)
               + Rlp.LengthOf(item.Data)
               + Rlp.LengthOf(item.ChainId ?? 0)
               + _accessListDecoder.GetLength(item.AccessList, RlpBehaviors.None);
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
}
