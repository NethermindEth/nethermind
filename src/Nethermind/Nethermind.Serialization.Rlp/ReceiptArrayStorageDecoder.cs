// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using System;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp;

[Rlp.SkipGlobalRegistration]
public sealed class ReceiptArrayStorageDecoder(bool compactEncoding = true) : RlpDecoder<TxReceipt[]>
{
    public static readonly ReceiptArrayStorageDecoder Instance = new();

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ReceiptArrayStorageDecoder))]
    public ReceiptArrayStorageDecoder() : this(true) { }

    private static readonly ReceiptStorageDecoder Decoder = new();
    private static readonly CompactReceiptStorageDecoder CompactDecoder = CompactReceiptStorageDecoder.Instance;

    public const int CompactEncoding = 127;

    public override int GetLength(TxReceipt[] items, RlpBehaviors rlpBehaviors)
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

    protected override TxReceipt[] DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.PeekByte() == CompactEncoding)
        {
            decoderContext.ReadByte();
            return CompactDecoder.DecodeArray(ref decoderContext, RlpBehaviors.Storage | RlpBehaviors.AllowExtraBytes);
        }
        else
        {
            int startPosition = decoderContext.Position;
            try
            {
                return Decoder.DecodeArray(ref decoderContext, RlpBehaviors.Storage);
            }
            catch (RlpException)
            {
                decoderContext.Position = startPosition;
                return Decoder.DecodeArray(ref decoderContext);
            }
        }
    }

    public override void Encode<TWriter>(ref TWriter writer, TxReceipt[] items, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (items is null || items.Length == 0)
        {
            writer.WriteByte(Rlp.EmptyListByte);
            return;
        }

        if (compactEncoding && (rlpBehaviors & RlpBehaviors.Storage) != 0)
        {
            int totalLength = GetContentLength(items, rlpBehaviors);
            writer.WriteByte(CompactEncoding);
            writer.StartSequence(totalLength - 1);

            for (int i = 0; i < items.Length; i++)
            {
                CompactDecoder.Encode(ref writer, items[i], rlpBehaviors);
            }
        }
        else
        {
            int totalLength = GetContentLength(items, rlpBehaviors);
            writer.StartSequence(totalLength);

            for (int i = 0; i < items.Length; i++)
            {
                Decoder.Encode(ref writer, items[i], rlpBehaviors);
            }
        }
    }

    public TxReceipt[] Decode(in Span<byte> receiptsData)
    {
        if (receiptsData.Length == 0 || receiptsData[0] == Rlp.EmptyListByte)
        {
            return [];
        }

        if (receiptsData.Length > 0 && receiptsData[0] == CompactEncoding)
        {
            RlpReader decoderContext = new(receiptsData[1..]);
            return CompactDecoder.DecodeArray(ref decoderContext, RlpBehaviors.Storage | RlpBehaviors.AllowExtraBytes);
        }
        else
        {
            RlpReader decoderContext = new(receiptsData);
            try
            {
                return Decoder.DecodeArray(ref decoderContext, RlpBehaviors.Storage);
            }
            catch (RlpException)
            {
                decoderContext.Position = 0;
                return Decoder.DecodeArray(ref decoderContext);
            }
        }
    }

    public TxReceipt DeserializeReceiptObsolete(Hash256 hash, Span<byte> receiptData)
    {
        RlpReader context = new(receiptData);
        try
        {
            TxReceipt receipt = Decoder.Decode(ref context, RlpBehaviors.Storage);
            receipt.TxHash = hash;
            return receipt;
        }
        catch (RlpException)
        {
            context.Position = 0;
            TxReceipt receipt = Decoder.Decode(ref context);
            receipt.TxHash = hash;
            return receipt;
        }
    }

    public static bool IsCompactEncoding(Span<byte> receiptsData) => receiptsData.Length > 0 && receiptsData[0] == CompactEncoding;

    public IReceiptRefDecoder GetRefDecoder(Span<byte> receiptsData) =>
        IsCompactEncoding(receiptsData)
            ? CompactDecoder
            : Decoder;
}
