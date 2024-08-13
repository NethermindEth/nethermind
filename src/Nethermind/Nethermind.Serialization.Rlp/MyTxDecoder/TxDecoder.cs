using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp.MyTxDecoder;

public sealed class MyTxDecoder : IRlpStreamDecoder<Transaction>, IRlpValueDecoder<Transaction>
{
    private readonly Dictionary<byte, AbstractTxDecoder> _decoders;

    private MyTxDecoder(bool lazyHash)
    {
        _decoders = new() {
            { (byte)TxType.Legacy, new LegacyTxDecoder(lazyHash) },
            { (byte)TxType.AccessList, new AccessListTxDecoder(lazyHash) },
            { (byte)TxType.EIP1559, new EIP1559TxDecoder(lazyHash) },
            { (byte)TxType.Blob, new BlobTxDecoder(lazyHash) },
            { (byte)TxType.DepositTx, new OptimismTxDecoder(lazyHash) }
        };
    }

    public readonly MyTxDecoder Instance = new(lazyHash: true);

    public readonly MyTxDecoder InstanceWithoutLazyHash = new(lazyHash: false);

    private AbstractTxDecoder DecoderFor(TxType txType)
    {
        if (!_decoders.TryGetValue((byte)txType, out AbstractTxDecoder decoder))
        {
            throw new InvalidOperationException("Unexpected TxType");
        }
        return decoder;
    }

    public Transaction Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.IsNextItemNull())
        {
            rlpStream.ReadByte();
            return null;
        }

        Span<byte> transactionSequence = rlpStream.PeekNextItem();
        TxType txType = (TxType)0xFF; // TODO: UNKNOWN

        if (rlpBehaviors.HasFlag(RlpBehaviors.SkipTypedWrapping))
        {
            if (rlpStream.PeekByte() <= 0x7F) // it is typed transactions
            {
                transactionSequence = rlpStream.Peek(rlpStream.Length);
                txType = (TxType)rlpStream.ReadByte();
                if (txType == TxType.Legacy)
                {
                    throw new RlpException("Legacy transactions are not allowed in EIP-2718 Typed Transaction Envelope.");
                }
            }
        }
        else if (!rlpStream.IsSequenceNext())
        {
            transactionSequence = rlpStream.Peek(rlpStream.ReadPrefixAndContentLength().ContentLength);
            txType = (TxType)rlpStream.ReadByte();
            if (txType == TxType.Legacy)
            {
                throw new RlpException("Legacy transactions are not allowed in EIP-2718 Typed Transaction Envelope.");
            }

        }

        AbstractTxDecoder decoder = DecoderFor(txType);
        return decoder.Decode(transactionSequence, rlpStream, rlpBehaviors);
    }

    public Transaction? Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemNull())
        {
            decoderContext.ReadByte();
            return null;
        }

        int txSequenceStart = decoderContext.Position;
        ReadOnlySpan<byte> transactionSequence = decoderContext.PeekNextItem();

        TxType txType = (TxType)0xFF; // TODO: UNKNOWN
        if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping)
        {
            byte firstByte = decoderContext.PeekByte();
            if (firstByte <= 0x7f) // it is typed transactions
            {
                txSequenceStart = decoderContext.Position;
                transactionSequence = decoderContext.Peek(decoderContext.Length);
                txType = (TxType)decoderContext.ReadByte();
            }
        }
        else
        {
            if (!decoderContext.IsSequenceNext())
            {
                (int _, int ContentLength) = decoderContext.ReadPrefixAndContentLength();
                txSequenceStart = decoderContext.Position;
                transactionSequence = decoderContext.Peek(ContentLength);
                txType = (TxType)decoderContext.ReadByte();
            }
        }

        AbstractTxDecoder decoder = DecoderFor(txType);
        return decoder.Decode(txSequenceStart, transactionSequence, ref decoderContext, rlpBehaviors);
    }

    public void Encode(RlpStream stream, Transaction? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        AbstractTxDecoder decoder = DecoderFor(item.Type);
        decoder.Encode(item, stream, rlpBehaviors);
    }

    public Rlp EncodeTx(Transaction? item, bool forSigning, bool isEip155Enabled, ulong chainId, RlpBehaviors rlpBehaviors)
    {
        AbstractTxDecoder decoder = DecoderFor(item.Type);
        return decoder.EncodeTx(item, forSigning, isEip155Enabled, chainId, rlpBehaviors);
    }

    public int GetLength(Transaction item, RlpBehaviors rlpBehaviors)
    {
        AbstractTxDecoder decoder = DecoderFor(item.Type);
        return decoder.GetLength(item, rlpBehaviors);
    }
}
