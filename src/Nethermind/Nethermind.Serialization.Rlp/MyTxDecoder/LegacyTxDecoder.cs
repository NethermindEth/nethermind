
// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp.MyTxDecoder;

public sealed class LegacyTxDecoder(bool lazyHash = true) : AbstractTxDecoder
{
    public override Transaction Decode(Span<byte> transactionSequence, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
    {
        Transaction transaction = new()
        {
            Type = TxType.Legacy
        };

        int transactionLength = rlpStream.ReadSequenceLength();
        int lastCheck = rlpStream.Position + transactionLength;

        DecodePayloadWithoutSig(transaction, rlpStream);

        if (rlpStream.Position < lastCheck)
        {
            DecodeSignature(transaction, rlpStream, rlpBehaviors);
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            rlpStream.Check(lastCheck);
        }

        if ((rlpBehaviors & RlpBehaviors.ExcludeHashes) == 0)
        {
            if (lazyHash && transactionSequence.Length <= TxDecoder.MaxDelayedHashTxnSize)
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

    public override Transaction Decode(int txSequenceStart, ReadOnlySpan<byte> transactionSequence, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
    {
        Transaction transaction = new()
        {
            Type = TxType.Legacy
        };

        int transactionLength = decoderContext.ReadSequenceLength();
        int lastCheck = decoderContext.Position + transactionLength;

        DecodePayloadWithoutSig(transaction, ref decoderContext);

        if (decoderContext.Position < lastCheck)
        {
            DecodeSignature(transaction, ref decoderContext, rlpBehaviors);
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
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

        return transaction;
    }

    public override Rlp EncodeTx(Transaction? item, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item?.Type != TxType.Legacy) { throw new InvalidOperationException("Unexpected TxType"); }

        RlpStream rlpStream = new(GetTxLength(item, forSigning, isEip155Enabled, chainId));
        Encode(item, rlpStream, forSigning, isEip155Enabled, chainId);
        return new Rlp(rlpStream.Data.ToArray());
    }

    public override void Encode(Transaction? item, RlpStream stream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item?.Type != TxType.Legacy) { throw new InvalidOperationException("Unexpected TxType"); }

        Encode(item, stream, forSigning: false, isEip155Enabled: false, chainId: 0);
    }

    private static void Encode(Transaction? item, RlpStream stream, bool forSigning, bool isEip155Enabled, ulong chainId = 0)
    {
        if (item is null)
        {
            stream.WriteByte(Rlp.NullObjectByte);
            return;
        }

        bool includeSigChainIdHack = isEip155Enabled && chainId != 0;
        int contentLength = GetContentLength(item, forSigning, isEip155Enabled, chainId);

        stream.StartSequence(contentLength);
        EncodePayloadWithoutSignature(item, stream);
        EncodeSignature(stream, item, forSigning, chainId, includeSigChainIdHack);
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

    public override int GetLength(Transaction tx, RlpBehaviors rlpBehaviors)
    {
        if (tx.Type != TxType.Legacy) { throw new InvalidOperationException("Unexpected TxType"); }

        int txContentLength = GetContentLength(tx, false);
        int txPayloadLength = Rlp.LengthOfSequence(txContentLength);

        return txPayloadLength;
    }

    private static int GetTxLength(Transaction tx, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
    {
        int txContentLength = GetContentLength(tx, forSigning, isEip155Enabled, chainId);
        int txPayloadLength = Rlp.LengthOfSequence(txContentLength);

        return txPayloadLength;
    }

    private static void DecodePayloadWithoutSig(Transaction transaction, RlpStream rlpStream)
    {
        transaction.Nonce = rlpStream.DecodeUInt256();
        transaction.GasPrice = rlpStream.DecodeUInt256();
        transaction.GasLimit = rlpStream.DecodeLong();
        transaction.To = rlpStream.DecodeAddress();
        transaction.Value = rlpStream.DecodeUInt256();
        transaction.Data = rlpStream.DecodeByteArray();
    }

    private static void DecodePayloadWithoutSig(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext)
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
        transaction.Signature = SignatureBuilder.FromBytes(v, rBytes, sBytes, rlpBehaviors);
    }

    private static void DecodeSignature(Transaction transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
    {
        ulong v = decoderContext.DecodeULong();
        ReadOnlySpan<byte> rBytes = decoderContext.DecodeByteArraySpan();
        ReadOnlySpan<byte> sBytes = decoderContext.DecodeByteArraySpan();
        transaction.Signature = SignatureBuilder.FromBytes(v, rBytes, sBytes, rlpBehaviors);
    }

    private static void EncodePayloadWithoutSignature(Transaction item, RlpStream stream)
    {
        stream.Encode(item.Nonce);
        stream.Encode(item.GasPrice);
        stream.Encode(item.GasLimit);
        stream.Encode(item.To);
        stream.Encode(item.Value);
        stream.Encode(item.Data);
    }
}
