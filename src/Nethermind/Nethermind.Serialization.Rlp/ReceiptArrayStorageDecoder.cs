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

    public override int GetLength(TxReceipt[]? items, RlpBehaviors rlpBehaviors)
    {
        if (items is null || items.Length == 0)
        {
            return 1;
        }

        int contentLength = GetContentLength(items, rlpBehaviors);
        if (compactEncoding && (rlpBehaviors & RlpBehaviors.Storage) != 0)
        {
            return Rlp.LengthOfSequence(contentLength - 1) + 2;
        }

        return Rlp.LengthOfSequence(contentLength);
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
            return TakeCompletePrefix(CompactDecoder.DecodeArray(
                ref decoderContext,
                RlpBehaviors.Storage | RlpBehaviors.AllowExtraBytes));
        }
        else
        {
            int startPosition = decoderContext.Position;
            try
            {
                return TakeCompletePrefix(Decoder.DecodeArray(ref decoderContext, RlpBehaviors.Storage));
            }
            catch (RlpException)
            {
                decoderContext.Position = startPosition;
                return TakeCompletePrefix(Decoder.DecodeArray(ref decoderContext));
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

        if (receiptsData[0] == CompactEncoding)
        {
            RlpReader decoderContext = new(receiptsData[1..]);
            return TakeCompletePrefix(CompactDecoder.DecodeArray(
                ref decoderContext,
                RlpBehaviors.Storage | RlpBehaviors.AllowExtraBytes));
        }
        else
        {
            RlpReader decoderContext = new(receiptsData);
            try
            {
                return TakeCompletePrefix(Decoder.DecodeArray(ref decoderContext, RlpBehaviors.Storage));
            }
            catch (RlpException)
            {
                decoderContext.Position = 0;
                return TakeCompletePrefix(Decoder.DecodeArray(ref decoderContext));
            }
        }
    }

    public TxReceipt?[] DecodeAllowingMissing(in Span<byte> receiptsData)
    {
        if (receiptsData.Length == 0 || receiptsData[0] == Rlp.EmptyListByte)
        {
            return [];
        }

        if (receiptsData[0] == CompactEncoding)
        {
            RlpReader decoderContext = new(receiptsData[1..]);
            return DecodeArrayAllowingMissingReceipts(
                ref decoderContext,
                CompactDecoder,
                RlpBehaviors.Storage | RlpBehaviors.AllowExtraBytes,
                includeTrailingItems: true);
        }

        RlpReader reader = new(receiptsData);
        try
        {
            return DecodeArrayAllowingMissingReceipts(ref reader, Decoder, RlpBehaviors.Storage);
        }
        catch (RlpException)
        {
            reader.Position = 0;
            return DecodeArrayAllowingMissingReceipts(ref reader, Decoder, RlpBehaviors.None);
        }
    }

    private static TxReceipt?[] DecodeArrayAllowingMissingReceipts(
        ref RlpReader decoderContext,
        RlpDecoder<TxReceipt> decoder,
        RlpBehaviors rlpBehaviors,
        bool includeTrailingItems = false)
    {
        int declaredEnd = decoderContext.ReadSequenceLength() + decoderContext.Position;
        int length = decoderContext.PeekNumberOfItemsRemaining(
            declaredEnd,
            RlpLimit.DefaultLimit.Limit + 1);
        decoderContext.GuardLimit(length);
        TxReceipt?[] result = new TxReceipt?[length];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = decoder.Decode(ref decoderContext, rlpBehaviors);
        }

        // The persisted compact format excludes its final byte from the outer sequence length.
        // A missing final receipt is therefore the one valid item that starts at the boundary.
        if (includeTrailingItems &&
            decoderContext.Position == declaredEnd &&
            decoderContext.Position < decoderContext.Length &&
            decoderContext.PeekByte() == Rlp.EmptyListByte)
        {
            decoderContext.ReadByte();
            Array.Resize(ref result, result.Length + 1);
        }

        if (!includeTrailingItems && (rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
        {
            decoderContext.Check(declaredEnd);
        }

        return result;
    }

    private static TxReceipt[] TakeCompletePrefix(TxReceipt?[] receipts)
    {
        int missingIndex = Array.IndexOf(receipts, null);
        if (missingIndex < 0)
        {
            // Nullable annotations do not change the runtime array type; this path proved every element is present.
            return (TxReceipt[])(object)receipts;
        }

        return Array.ConvertAll(receipts[..missingIndex], static receipt => receipt!);
    }

    public TxReceipt DeserializeReceiptObsolete(Hash256 hash, Span<byte> receiptData)
    {
        RlpReader context = new(receiptData);
        try
        {
            TxReceipt receipt = Decoder.DecodeGuardNotNull(ref context, RlpBehaviors.Storage);
            receipt.TxHash = hash;
            return receipt;
        }
        catch (RlpException)
        {
            context.Position = 0;
            TxReceipt receipt = Decoder.DecodeGuardNotNull(ref context);
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
