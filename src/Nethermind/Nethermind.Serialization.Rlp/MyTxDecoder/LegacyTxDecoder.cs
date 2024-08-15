
// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp.MyTxDecoder;

public sealed class LegacyTxDecoder(bool lazyHash = true, Func<Transaction>? transactionFactory = null) : ITxDecoder
{
    private readonly bool _lazyHash = lazyHash;
    private readonly Func<Transaction> _createTransaction = transactionFactory ?? (() => new Transaction());

    public Transaction? Decode(Span<byte> transactionSequence, RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        Transaction transaction = _createTransaction();
        transaction.Type = TxType.Legacy;

        int transactionLength = rlpStream.ReadSequenceLength();
        int lastCheck = rlpStream.Position + transactionLength;

        DecodePayload(transaction, rlpStream);

        if (rlpStream.Position < lastCheck)
        {
            DecodeSignature(transaction, rlpStream, rlpBehaviors);
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
        {
            rlpStream.Check(lastCheck);
        }

        if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
        {
            if (_lazyHash && transactionSequence.Length <= ITxDecoder.MaxDelayedHashTxnSize)
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
        transaction.Type = TxType.Legacy;

        int transactionLength = decoderContext.ReadSequenceLength();
        int lastCheck = decoderContext.Position + transactionLength;

        DecodePayload(transaction, ref decoderContext);

        if (decoderContext.Position < lastCheck)
        {
            DecodeSignature(transaction, ref decoderContext, rlpBehaviors);
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
        {
            decoderContext.Check(lastCheck);
        }

        if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
        {
            if (_lazyHash && transactionSequence.Length <= ITxDecoder.MaxDelayedHashTxnSize)
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

    public void Encode(Transaction? transaction, RlpStream stream, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        bool includeSigChainIdHack = isEip155Enabled && chainId != 0;
        int contentLength = GetContentLength(transaction, forSigning, isEip155Enabled, chainId);

        stream.StartSequence(contentLength);
        EncodePayload(transaction, stream);
        EncodeSignature(transaction, stream, forSigning, chainId, includeSigChainIdHack);
    }

    public int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        int txContentLength = GetContentLength(transaction, forSigning, isEip155Enabled, chainId);
        int txPayloadLength = Rlp.LengthOfSequence(txContentLength);

        return txPayloadLength;
    }

    private static void DecodePayload(Transaction transaction, RlpStream rlpStream)
    {
        transaction.Nonce = rlpStream.DecodeUInt256();
        transaction.GasPrice = rlpStream.DecodeUInt256();
        transaction.GasLimit = rlpStream.DecodeLong();
        transaction.To = rlpStream.DecodeAddress();
        transaction.Value = rlpStream.DecodeUInt256();
        transaction.Data = rlpStream.DecodeByteArray();
    }

    private static void DecodePayload(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext)
    {
        transaction.Nonce = decoderContext.DecodeUInt256();
        transaction.GasPrice = decoderContext.DecodeUInt256();
        transaction.GasLimit = decoderContext.DecodeLong();
        transaction.To = decoderContext.DecodeAddress();
        transaction.Value = decoderContext.DecodeUInt256();
        transaction.Data = decoderContext.DecodeByteArrayMemory();
    }

    private static void DecodeSignature(Transaction transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        ulong v = rlpStream.DecodeULong();
        ReadOnlySpan<byte> rBytes = rlpStream.DecodeByteArraySpan();
        ReadOnlySpan<byte> sBytes = rlpStream.DecodeByteArraySpan();
        transaction.Signature = SignatureBuilder.FromBytes(v, rBytes, sBytes, rlpBehaviors) ?? transaction.Signature;
    }

    private static void DecodeSignature(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
    {
        ulong v = decoderContext.DecodeULong();
        ReadOnlySpan<byte> rBytes = decoderContext.DecodeByteArraySpan();
        ReadOnlySpan<byte> sBytes = decoderContext.DecodeByteArraySpan();
        transaction.Signature = SignatureBuilder.FromBytes(v, rBytes, sBytes, rlpBehaviors) ?? transaction.Signature;
    }

    private static void EncodePayload(Transaction transaction, RlpStream stream)
    {
        stream.Encode(transaction.Nonce);
        stream.Encode(transaction.GasPrice);
        stream.Encode(transaction.GasLimit);
        stream.Encode(transaction.To);
        stream.Encode(transaction.Value);
        stream.Encode(transaction.Data);
    }

    private static void EncodeSignature(Transaction transaction, RlpStream stream, bool forSigning, ulong chainId, bool includeSigChainIdHack)
    {
        if (forSigning)
        {
            if (includeSigChainIdHack)
            {
                stream.Encode(chainId);
                stream.Encode(Rlp.OfEmptyByteArray);
                stream.Encode(Rlp.OfEmptyByteArray);
            }
        }
        else
        {
            if (transaction.Signature is null)
            {
                stream.Encode(0);
                stream.Encode(Bytes.Empty);
                stream.Encode(Bytes.Empty);
            }
            else
            {
                stream.Encode(transaction.Signature.V);
                stream.Encode(transaction.Signature.RAsSpan.WithoutLeadingZeros());
                stream.Encode(transaction.Signature.SAsSpan.WithoutLeadingZeros());
            }
        }
    }

    private static int GetContentLength(Transaction transaction, bool forSigning, bool isEip155Enabled = false, ulong chainId = 0)
    {
        int payloadLength = GetPayloadLength(transaction);
        int signatureLength = GetSignatureLength(transaction, forSigning, chainId, includeSigChainIdHack: isEip155Enabled && chainId != 0);

        return payloadLength + signatureLength;
    }

    private static int GetPayloadLength(Transaction transaction)
    {
        return Rlp.LengthOf(transaction.Nonce)
            + Rlp.LengthOf(transaction.GasPrice)
            + Rlp.LengthOf(transaction.GasLimit)
            + Rlp.LengthOf(transaction.To)
            + Rlp.LengthOf(transaction.Value)
            + Rlp.LengthOf(transaction.Data);
    }

    private static int GetSignatureLength(Transaction transaction, bool forSigning, ulong chainId, bool includeSigChainIdHack)
    {
        int contentLength = 0;

        if (forSigning)
        {
            if (includeSigChainIdHack)
            {
                contentLength += Rlp.LengthOf(chainId);
                contentLength += 1;
                contentLength += 1;
            }
        }
        else
        {
            if (transaction.Signature is null)
            {
                contentLength += 1;
                contentLength += 1;
                contentLength += 1;
            }
            else
            {
                contentLength += Rlp.LengthOf(transaction.Signature.V);
                contentLength += Rlp.LengthOf(transaction.Signature.RAsSpan.WithoutLeadingZeros());
                contentLength += Rlp.LengthOf(transaction.Signature.SAsSpan.WithoutLeadingZeros());
            }
        }

        return contentLength;
    }
}
