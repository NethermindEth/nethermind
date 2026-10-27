// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nethermind.Serialization.Rlp;

[Rlp.SkipGlobalRegistration]
public sealed class ReceiptArrayStorageDecoder(bool compactEncoding = true, bool storeTxHashes = true) : RlpDecoder<TxReceipt[]>
{
    public static readonly ReceiptArrayStorageDecoder Instance = new();

    /// <summary>Writes compact receipts with the <see cref="CompactEncoding"/> marker only, which earlier versions read.</summary>
    public static readonly ReceiptArrayStorageDecoder WithoutTxHashes = new(compactEncoding: true, storeTxHashes: false);

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ReceiptArrayStorageDecoder))]
    public ReceiptArrayStorageDecoder() : this(true, true) { }

    private static readonly ReceiptStorageDecoder Decoder = new();
    private static readonly CompactReceiptStorageDecoder CompactDecoder = CompactReceiptStorageDecoder.Instance;

    public const int CompactEncoding = 127;

    /// <summary>
    /// Compact receipts preceded by an RLP string of the block's transaction hashes in transaction order, so that
    /// log queries do not read the block body to recover them. The rest of the value equals the
    /// <see cref="CompactEncoding"/> value without its marker.
    /// </summary>
    public const int CompactEncodingWithTxHashes = 126;

    public override void PopulateLength(TxReceipt[]? items, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (items is null || items.Length == 0)
        {
            lengths.Push(1);
            return;
        }

        bool isCompact = compactEncoding && (rlpBehaviors & RlpBehaviors.Storage) != 0;
        IRlpDecoder<TxReceipt> decoder = isCompact ? CompactDecoder : Decoder;
        int contentLength = 0;
        for (int i = items.Length - 1; i >= 0; i--)
        {
            contentLength += decoder.PopulateAndPeek(items[i], ref lengths, rlpBehaviors);
        }

        lengths.Push(contentLength);
        if (isCompact)
        {
            int txHashesLength = WritesTxHashes(items) ? Rlp.LengthOfByteString(items.Length * Hash256.Size, byte.MaxValue) : 0;
            lengths.Push(Rlp.LengthOfSequence(contentLength - 1) + 2 + txHashesLength);
            return;
        }

        lengths.Push(Rlp.LengthOfSequence(contentLength));
    }

    protected override TxReceipt[] DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (TrySkipCompactPrefix(ref decoderContext, out _))
        {
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

    public override void Encode<TWriter>(TxReceipt[]? items, ref LengthStack lengths, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        lengths.Pop();
        if (items is null || items.Length == 0)
        {
            writer.WriteByte(Rlp.EmptyListByte);
            return;
        }

        int totalLength = lengths.Pop();
        if (compactEncoding && (rlpBehaviors & RlpBehaviors.Storage) != 0)
        {
            if (WritesTxHashes(items))
            {
                writer.WriteByte(CompactEncodingWithTxHashes);
                writer.StartByteArray(items.Length * Hash256.Size, false);
                for (int i = 0; i < items.Length; i++)
                {
                    writer.Write(items[i].TxHash!.Bytes);
                }
            }
            else
            {
                writer.WriteByte(CompactEncoding);
            }

            writer.StartSequence(totalLength - 1);

            for (int i = 0; i < items.Length; i++)
            {
                CompactDecoder.Encode(items[i], ref lengths, ref writer, rlpBehaviors);
            }
        }
        else
        {
            writer.StartSequence(totalLength);

            for (int i = 0; i < items.Length; i++)
            {
                Decoder.Encode(items[i], ref lengths, ref writer, rlpBehaviors);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TxReceipt[] Decode(in Span<byte> receiptsData) => Decode(in receiptsData, RlpBehaviors.None);

    /// <param name="receiptsData">The stored receipts.</param>
    /// <param name="compactBehaviors">Extra behaviors for the compact encoding, e.g. <see cref="RlpBehaviors.Eip7668Receipts"/>.</param>
    public TxReceipt[] Decode(in Span<byte> receiptsData, RlpBehaviors compactBehaviors)
    {
        if (receiptsData.Length == 0 || receiptsData[0] == Rlp.EmptyListByte)
        {
            return [];
        }

        RlpReader compactReader = new(receiptsData);
        if (TrySkipCompactPrefix(ref compactReader, out _))
        {
            RlpReader decoderContext = new(receiptsData[compactReader.Position..]);
            return TakeCompletePrefix(CompactDecoder.DecodeArray(
                ref decoderContext,
                RlpBehaviors.Storage | RlpBehaviors.AllowExtraBytes | compactBehaviors));
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

        RlpReader compactReader = new(receiptsData);
        if (TrySkipCompactPrefix(ref compactReader, out _))
        {
            RlpReader decoderContext = new(receiptsData[compactReader.Position..]);
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

    public static bool IsCompactEncoding(Span<byte> receiptsData) =>
        receiptsData.Length > 0 && receiptsData[0] is CompactEncoding or CompactEncodingWithTxHashes;

    /// <summary>Whether the value is compact and does not carry the transaction hashes yet.</summary>
    public static bool IsCompactEncodingWithoutTxHashes(ReadOnlySpan<byte> receiptsData) =>
        receiptsData.Length > 0 && receiptsData[0] == CompactEncoding;

    /// <summary>
    /// Skips the marker of a compact value and, when present, the stored transaction hashes, leaving the reader at
    /// the receipt list.
    /// </summary>
    /// <returns><see langword="false"/> and an unchanged reader when the value is not compact.</returns>
    public static bool TrySkipCompactPrefix(scoped ref RlpReader reader, out ReadOnlySpan<byte> txHashes)
    {
        txHashes = default;
        if (reader.Position >= reader.Length) return false;

        switch (reader.PeekByte())
        {
            case CompactEncoding:
                reader.ReadByte();
                return true;
            case CompactEncodingWithTxHashes:
                reader.ReadByte();
                (_, int contentLength) = reader.ReadPrefixAndContentLength();
                if (contentLength % Hash256.Size != 0)
                {
                    throw new RlpException($"Stored transaction hashes length {contentLength} is not a multiple of {Hash256.Size}.");
                }

                txHashes = reader.Read(contentLength);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Builds the <see cref="CompactEncodingWithTxHashes"/> value from a <see cref="CompactEncoding"/> value without
    /// decoding the receipts: the receipt bytes are copied as they are.
    /// </summary>
    public static byte[] AddTxHashes(ReadOnlySpan<byte> compactReceiptsData, ReadOnlySpan<byte> txHashes)
    {
        if (!IsCompactEncodingWithoutTxHashes(compactReceiptsData))
        {
            throw new ArgumentException("Only a compact value without transaction hashes can take them.", nameof(compactReceiptsData));
        }

        ReadOnlySpan<byte> receipts = compactReceiptsData[1..];
        byte[] result = new byte[1 + Rlp.LengthOfByteString(txHashes.Length, byte.MaxValue) + receipts.Length];
        RlpWriter writer = new(result);
        Write(ref writer, txHashes, receipts);
        return result;

        static void Write<TWriter>(ref TWriter writer, ReadOnlySpan<byte> txHashes, ReadOnlySpan<byte> receipts)
            where TWriter : struct, IRlpWriteBackend, allows ref struct
        {
            writer.WriteByte(CompactEncodingWithTxHashes);
            writer.StartByteArray(txHashes.Length, false);
            writer.Write(txHashes);
            writer.Write(receipts);
        }
    }

    private bool WritesTxHashes(TxReceipt[] items)
    {
        if (!storeTxHashes) return false;

        for (int i = 0; i < items.Length; i++)
        {
            if (items[i]?.TxHash is null) return false;
        }

        return true;
    }

    public IReceiptRefDecoder GetRefDecoder(Span<byte> receiptsData) =>
        IsCompactEncoding(receiptsData)
            ? CompactDecoder
            : Decoder;
}
