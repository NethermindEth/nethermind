
// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp.MyTxDecoder;

public sealed class LegacyTxDecoder(bool lazyHash = true) : ITxDecoder
{
    public const int MaxDelayedHashTxnSize = 32768;

    public Transaction? Decode(Span<byte> transactionSequence, RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        Transaction transaction = new()
        {
            Type = TxType.Legacy
        };

        int transactionLength = rlpStream.ReadSequenceLength();
        int lastCheck = rlpStream.Position + transactionLength;

        DecodeLegacyPayloadWithoutSig(transaction, rlpStream);

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
        transaction.Type = TxType.Legacy;

        int transactionLength = decoderContext.ReadSequenceLength();
        int lastCheck = decoderContext.Position + transactionLength;

        DecodeLegacyPayloadWithoutSig(transaction, ref decoderContext);

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
        bool includeSigChainIdHack = isEip155Enabled && chainId != 0;
        int contentLength = GetContentLength(item, forSigning, isEip155Enabled, chainId);

        stream.StartSequence(contentLength);
        EncodeLegacyWithoutPayload(item, stream);
        EncodeSignature(stream, item, forSigning, chainId, includeSigChainIdHack);
    }

    public int GetTxLength(Transaction tx, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        int txContentLength = GetContentLength(tx, forSigning, isEip155Enabled, chainId);
        int txPayloadLength = Rlp.LengthOfSequence(txContentLength);

        return txPayloadLength;
    }

    private static void DecodeLegacyPayloadWithoutSig(Transaction transaction, RlpStream rlpStream)
    {
        transaction.Nonce = rlpStream.DecodeUInt256();
        transaction.GasPrice = rlpStream.DecodeUInt256();
        transaction.GasLimit = rlpStream.DecodeLong();
        transaction.To = rlpStream.DecodeAddress();
        transaction.Value = rlpStream.DecodeUInt256();
        transaction.Data = rlpStream.DecodeByteArray();
    }

    private static void DecodeLegacyPayloadWithoutSig(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext)
    {
        transaction.Nonce = decoderContext.DecodeUInt256();
        transaction.GasPrice = decoderContext.DecodeUInt256();
        transaction.GasLimit = decoderContext.DecodeLong();
        transaction.To = decoderContext.DecodeAddress();
        transaction.Value = decoderContext.DecodeUInt256();
        transaction.Data = decoderContext.DecodeByteArrayMemory();
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
            Signature signature = new(rBytes, sBytes, v);
            transaction.Signature = signature;
        }
    }

    private static void EncodeLegacyWithoutPayload(Transaction item, RlpStream stream)
    {
        stream.Encode(item.Nonce);
        stream.Encode(item.GasPrice);
        stream.Encode(item.GasLimit);
        stream.Encode(item.To);
        stream.Encode(item.Value);
        stream.Encode(item.Data);
    }

    private static void EncodeSignature(RlpStream stream, Transaction item, bool forSigning, ulong chainId, bool includeSigChainIdHack)
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
            if (item.Signature is null)
            {
                stream.Encode(0);
                stream.Encode(Bytes.Empty);
                stream.Encode(Bytes.Empty);
            }
            else
            {
                stream.Encode(item.Signature.V);
                stream.Encode(item.Signature.RAsSpan.WithoutLeadingZeros());
                stream.Encode(item.Signature.SAsSpan.WithoutLeadingZeros());
            }
        }
    }

    private static int GetContentLength(Transaction item, bool forSigning, bool isEip155Enabled = false, ulong chainId = 0)
    {
        bool includeSigChainIdHack = isEip155Enabled && chainId != 0;
        int contentLength = GetLegacyContentLength(item);
        contentLength += GetSignatureContentLength(item, forSigning, chainId, includeSigChainIdHack);

        return contentLength;
    }

    private static int GetLegacyContentLength(Transaction item)
    {
        return Rlp.LengthOf(item.Nonce)
            + Rlp.LengthOf(item.GasPrice)
            + Rlp.LengthOf(item.GasLimit)
            + Rlp.LengthOf(item.To)
            + Rlp.LengthOf(item.Value)
            + Rlp.LengthOf(item.Data);
    }

    private static int GetSignatureContentLength(Transaction item, bool forSigning, ulong chainId, bool includeSigChainIdHack)
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
            if (item.Signature is null)
            {
                contentLength += 1;
                contentLength += 1;
                contentLength += 1;
            }
            else
            {
                contentLength += Rlp.LengthOf(item.Signature.V);
                contentLength += Rlp.LengthOf(item.Signature.RAsSpan.WithoutLeadingZeros());
                contentLength += Rlp.LengthOf(item.Signature.SAsSpan.WithoutLeadingZeros());
            }
        }

        return contentLength;
    }
}
