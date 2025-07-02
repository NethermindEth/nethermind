// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp;

[Rlp.SkipGlobalRegistration]
public class ReceiptArrayStorageDecoder(bool compactEncoding = true) : IRlpStreamDecoder<TxReceipt[]>
{
    public static readonly ReceiptArrayStorageDecoder Instance = new();

    private static readonly IRlpStreamDecoder<TxReceipt> Decoder = Rlp.GetStreamDecoder<TxReceipt>(RlpDecoderKey.LegacyStorage);
    private static readonly IRlpValueDecoder<TxReceipt> ValueDecoder = Rlp.GetValueDecoder<TxReceipt>(RlpDecoderKey.LegacyStorage);

    private static readonly IRlpStreamDecoder<TxReceipt> CompactDecoder = Rlp.GetStreamDecoder<TxReceipt>(RlpDecoderKey.Storage);
    private static readonly IRlpValueDecoder<TxReceipt> CompactValueDecoder = Rlp.GetValueDecoder<TxReceipt>(RlpDecoderKey.Storage);

    public const int CompactEncoding = 127;

    public int GetLength(TxReceipt[] items, RlpBehaviors rlpBehaviors)
    {
        if (items is null || items.Length == 0)
        {
            return 1;
        }

        int bufferLength = Rlp.LengthOfSequence(GetContentLength(items, rlpBehaviors));
        if (compactEncoding && (rlpBehaviors & RlpBehaviors.Storage) != 0)
        {
            bufferLength++;
        }
        return bufferLength;
    }

    private int GetContentLength(TxReceipt[] items, RlpBehaviors rlpBehaviors)
    {
        if (compactEncoding && (rlpBehaviors & RlpBehaviors.Storage) != 0)
        {
            int totalLength = 0;
            for (int i = 0; i < items.Length; i++)
            {
                totalLength += CompactDecoder.GetLength(items[i], rlpBehaviors);
            }

            return totalLength;
        }
        else
        {
            int totalLength = 0;
            for (int i = 0; i < items.Length; i++)
            {
                totalLength += Decoder.GetLength(items[i], rlpBehaviors);
            }

            return totalLength;
        }
    }

    public TxReceipt[] Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.PeekByte() == CompactEncoding)
        {
            rlpStream.ReadByte();
            return CompactDecoder.DecodeArray(rlpStream, RlpBehaviors.Storage);
        }
        else
        {
            return Decoder.DecodeArray(rlpStream, RlpBehaviors.Storage);
        }
    }

    public void Encode(RlpStream stream, TxReceipt[] items, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (items is null || items.Length == 0)
        {
            stream.EncodeNullObject();
            return;
        }

        if (compactEncoding && (rlpBehaviors & RlpBehaviors.Storage) != 0)
        {
            int totalLength = GetContentLength(items, rlpBehaviors);
            stream.WriteByte(CompactEncoding);
            stream.StartSequence(totalLength - 1);

            for (int i = 0; i < items.Length; i++)
            {
                CompactDecoder.Encode(stream, items[i], rlpBehaviors);
            }
        }
        else
        {
            int totalLength = GetContentLength(items, rlpBehaviors);
            stream.StartSequence(totalLength);

            for (int i = 0; i < items.Length; i++)
            {
                Decoder.Encode(stream, items[i], rlpBehaviors);
            }
        }
    }

    public TxReceipt[] Decode(in Span<byte> receiptsData)
    {
        if (receiptsData.Length == 0 || receiptsData[0] == Rlp.NullObjectByte)
        {
            return [];
        }

        if (receiptsData.Length > 0 && receiptsData[0] == CompactEncoding)
        {
            var decoderContext = new Rlp.ValueDecoderContext(receiptsData[1..]);
            return CompactValueDecoder.DecodeArray(ref decoderContext, RlpBehaviors.Storage);
        }
        else
        {
            var decoderContext = new Rlp.ValueDecoderContext(receiptsData);
            try
            {
                return ValueDecoder.DecodeArray(ref decoderContext, RlpBehaviors.Storage);
            }
            catch (RlpException)
            {
                decoderContext.Position = 0;
                return ValueDecoder.DecodeArray(ref decoderContext);
            }
        }
    }

    public TxReceipt DeserializeReceiptObsolete(Hash256 hash, Span<byte> receiptData)
    {
        var context = new Rlp.ValueDecoderContext(receiptData);
        try
        {
            var receipt = ValueDecoder.Decode(ref context, RlpBehaviors.Storage);
            receipt.TxHash = hash;
            return receipt;
        }
        catch (RlpException)
        {
            context.Position = 0;
            var receipt = ValueDecoder.Decode(ref context);
            receipt.TxHash = hash;
            return receipt;
        }
    }

    public static bool IsCompactEncoding(Span<byte> receiptsData)
    {
        return receiptsData.Length > 0 && receiptsData[0] == CompactEncoding;
    }

    public IReceiptRefDecoder GetRefDecoder(Span<byte> receiptsData)
    {
        if (IsCompactEncoding(receiptsData))
        {
            return (IReceiptRefDecoder)CompactValueDecoder;
        }

        return (IReceiptRefDecoder)ValueDecoder;
    }
}
