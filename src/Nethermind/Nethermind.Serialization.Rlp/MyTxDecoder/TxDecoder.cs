using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp.MyTxDecoder;

sealed class MyTxDecoder : IRlpStreamDecoder<Transaction>
{
    private static readonly Dictionary<byte, AbstractTxDecoder> _decoders = new()
    {
        { (byte)TxType.Legacy, new LegacyTxDecoder() },
        { (byte)TxType.AccessList, new AccessListTxDecoder() },
        { (byte)TxType.EIP1559, new EIP1559TxDecoder() },
        { (byte)TxType.Blob, new BlobTxDecoder() },
        { (byte)TxType.DepositTx, new OptimismTxDecoder() }
    };

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

        if (!_decoders.TryGetValue((byte)txType, out AbstractTxDecoder decoder))
        {
            throw new InvalidOperationException("Unexpected TxType");
        }

        return decoder.Decode(transactionSequence, rlpStream, rlpBehaviors);
    }

    public void Encode(RlpStream stream, Transaction item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (!_decoders.TryGetValue((byte)item.Type, out AbstractTxDecoder decoder))
        {
            throw new InvalidOperationException("Unexpected TxType");
        }

        decoder.Encode(item, stream, rlpBehaviors);
    }

    public int GetLength(Transaction item, RlpBehaviors rlpBehaviors)
    {
        if (!_decoders.TryGetValue((byte)item.Type, out AbstractTxDecoder decoder))
        {
            throw new InvalidOperationException("Unexpected TxType");
        }

        return decoder.GetLength(item, rlpBehaviors);
    }
}
