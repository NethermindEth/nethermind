// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp;

[Rlp.SkipGlobalRegistration]
public class ReceiptArrayStorageDecoder : IRlpStreamDecoder<TxReceipt[]>
{
    public static readonly ReceiptArrayStorageDecoder Instance = new();
    private ReceiptStorageDecoder StorageDecoder = ReceiptStorageDecoder.Instance;
    private CompactReceiptStorageDecoder CompactReceiptStorageDecoder = CompactReceiptStorageDecoder.Instance;

    public const int CompactEncoding = 127;
    private bool _useCompactEncoding = true;

    public ReceiptArrayStorageDecoder(bool compactEncoding = true)
    {
        _useCompactEncoding = compactEncoding;
    }

    public int GetLength(TxReceipt[] items, RlpBehaviors rlpBehaviors)
    {
        if (items is null || items.Length == 0)
        {
            return 1;
        }

        int bufferLength = Rlp.LengthOfSequence(GetContentLength(items, rlpBehaviors));
        if (_useCompactEncoding && (rlpBehaviors & RlpBehaviors.Storage) != 0)
        {
            bufferLength++;
        }
        return bufferLength;
    }

    private int GetContentLength(TxReceipt[] items, RlpBehaviors rlpBehaviors)
    {
        if (_useCompactEncoding && (rlpBehaviors & RlpBehaviors.Storage) != 0)
        {
            int totalLength = 0;
            for (int i = 0; i < items.Length; i++)
            {
                totalLength += CompactReceiptStorageDecoder.GetLength(items[i], rlpBehaviors);
            }

            return totalLength;
        }
        else
        {
            int totalLength = 0;
            for (int i = 0; i < items.Length; i++)
            {
                totalLength += StorageDecoder.GetLength(items[i], rlpBehaviors);
            }

            return totalLength;
        }
    }

    public TxReceipt[] Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.PeekByte() == CompactEncoding)
        {
            rlpStream.ReadByte();
            return CompactReceiptStorageDecoder.DecodeArray(rlpStream, RlpBehaviors.Storage);
        }
        else
        {
            return StorageDecoder.DecodeArray(rlpStream, RlpBehaviors.Storage);
        }
    }

    public void Encode(RlpStream stream, TxReceipt[] items, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (items is null || items.Length == 0)
        {
            stream.EncodeNullObject();
            return;
        }

        if (_useCompactEncoding && (rlpBehaviors & RlpBehaviors.Storage) != 0)
        {
            int totalLength = GetContentLength(items, rlpBehaviors);
            stream.WriteByte(CompactEncoding);
            stream.StartSequence(totalLength - 1);

            for (int i = 0; i < items.Length; i++)
            {
                CompactReceiptStorageDecoder.Encode(stream, items[i], rlpBehaviors);
            }
        }
        else
        {
            int totalLength = GetContentLength(items, rlpBehaviors);
            stream.StartSequence(totalLength);

            for (int i = 0; i < items.Length; i++)
            {
                StorageDecoder.Encode(stream, items[i], rlpBehaviors);
            }
        }
    }

    public TxReceipt[] Decode(in Span<byte> receiptsData)
    {
        if (receiptsData.Length == 0 || receiptsData[0] == Rlp.NullObjectByte)
        {
            return Array.Empty<TxReceipt>();
        }

        if (receiptsData.Length > 0 && receiptsData[0] == CompactEncoding)
        {
            var decoderContext = new Rlp.ValueDecoderContext(receiptsData.Slice(1));
            return CompactReceiptStorageDecoder.DecodeArray(ref decoderContext, RlpBehaviors.Storage);
        }
        else
        {
            var decoderContext = new Rlp.ValueDecoderContext(receiptsData);
            try
            {
                return StorageDecoder.DecodeArray(ref decoderContext, RlpBehaviors.Storage);
            }
            catch (RlpException)
            {
                decoderContext.Position = 0;
                return StorageDecoder.DecodeArray(ref decoderContext);
            }
        }
    }

    public TxReceipt DeserializeReceiptObsolete(Keccak hash, Span<byte> receiptData)
    {
        var context = new Rlp.ValueDecoderContext(receiptData);
        try
        {
            var receipt = StorageDecoder.Decode(ref context, RlpBehaviors.Storage);
            receipt.TxHash = hash;
            return receipt;
        }
        catch (RlpException)
        {
            context.Position = 0;
            var receipt = StorageDecoder.Decode(ref context);
            receipt.TxHash = hash;
            return receipt;
        }
    }

    public bool IsCompactEncoding(Span<byte> receiptsData)
    {
        return receiptsData.Length > 0 && receiptsData[0] == CompactEncoding;
    }

    public IReceiptRefDecoder GetRefDecoder(Span<byte> receiptsData)
    {
        if (IsCompactEncoding(receiptsData))
        {
            return CompactReceiptStorageDecoder.Instance;
        }

        return ReceiptStorageDecoder.Instance;
    }
}
