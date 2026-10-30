// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
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

    public override int GetLength(TxReceipt[]? items, RlpBehaviors rlpBehaviors)
    {
        if (items is null || items.Length == 0)
        {
            return 1;
        }

        int contentLength = GetContentLength(items, rlpBehaviors);
        if (compactEncoding && (rlpBehaviors & RlpBehaviors.Storage) != 0)
        {
            int txHashesLength = WritesTxHashes(items) ? Rlp.LengthOfByteString(items.Length * Hash256.Size, byte.MaxValue) : 0;
            return Rlp.LengthOfSequence(contentLength - 1) + 2 + txHashesLength;
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

    protected override TxReceipt[] DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpBehaviors trusted = rlpBehaviors & RlpBehaviors.TrustedInput;
        if (TrySkipCompactPrefix(ref reader, out _))
        {
            return TakeCompletePrefix(DecodeCompact(ref reader, RlpBehaviors.Storage | RlpBehaviors.AllowExtraBytes | trusted));
        }
        else
        {
            RlpReader start = reader;
            try
            {
                return TakeCompletePrefix(Decoder.DecodeArray(ref reader, RlpBehaviors.Storage | trusted));
            }
            catch (RlpReaderException)
            {
                reader = start;
                return TakeCompletePrefix(Decoder.DecodeArray(ref reader, trusted));
            }
        }
    }

    public override void Encode(ref RlpWriter writer, TxReceipt[] items, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (items is null || items.Length == 0)
        {
            writer.EncodeNullObject();
            return;
        }

        if (compactEncoding && (rlpBehaviors & RlpBehaviors.Storage) != 0)
        {
            int totalLength = GetContentLength(items, rlpBehaviors);
            if (WritesTxHashes(items))
            {
                writer.WriteByte(CompactEncodingWithTxHashes);
                writer.StartByteArray(items.Length * Hash256.Size, false);
                for (int i = 0; i < items.Length; i++)
                {
                    writer.WriteEncoded(items[i].TxHash!.Bytes);
                }
            }
            else
            {
                writer.WriteByte(CompactEncoding);
            }

            // The stored compact format declares one byte less than its receipts take, so the prefix is written
            // without a sequence that checks the length.
            writer.WriteSequencePrefix(totalLength - 1);

            for (int i = 0; i < items.Length; i++)
            {
                CompactDecoder.Encode(ref writer, items[i], rlpBehaviors);
            }
        }
        else
        {
            int totalLength = GetContentLength(items, rlpBehaviors);
            RlpSequence sequence = writer.StartSequence(totalLength);

            for (int i = 0; i < items.Length; i++)
            {
                Decoder.Encode(ref writer, items[i], rlpBehaviors);
            }

            writer.EndSequence(sequence);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TxReceipt[] Decode(in Span<byte> receiptsData) => Decode(in receiptsData, RlpBehaviors.None);

    /// <param name="receiptsData">The stored receipts.</param>
    /// <param name="compactBehaviors">
    /// Extra behaviors for the compact encoding, e.g. <see cref="RlpBehaviors.Eip7668Receipts"/>.
    /// <see cref="RlpBehaviors.TrustedInput"/> applies to every encoding.
    /// </param>
    public TxReceipt[] Decode(in Span<byte> receiptsData, RlpBehaviors compactBehaviors)
    {
        if (receiptsData.Length == 0 || receiptsData[0] == Rlp.EmptyListByte)
        {
            return [];
        }

        RlpReader reader = new(receiptsData);
        if (TrySkipCompactPrefix(ref reader, out _))
        {
            return TakeCompletePrefix(DecodeCompact(ref reader, RlpBehaviors.Storage | RlpBehaviors.AllowExtraBytes | compactBehaviors));
        }
        else
        {
            RlpBehaviors trusted = compactBehaviors & RlpBehaviors.TrustedInput;
            try
            {
                return TakeCompletePrefix(Decoder.DecodeArray(ref reader, RlpBehaviors.Storage | trusted));
            }
            catch (RlpReaderException)
            {
                reader = new RlpReader(receiptsData);
                return TakeCompletePrefix(Decoder.DecodeArray(ref reader, trusted));
            }
        }
    }

    /// <inheritdoc cref="Decode(in Span{byte}, RlpBehaviors)"/>
    /// <param name="rlpBehaviors"><see cref="RlpBehaviors.TrustedInput"/> applies; the other behaviors are set by the encoding.</param>
    public TxReceipt?[] DecodeAllowingMissing(in Span<byte> receiptsData, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (receiptsData.Length == 0 || receiptsData[0] == Rlp.EmptyListByte)
        {
            return [];
        }

        RlpBehaviors trusted = rlpBehaviors & RlpBehaviors.TrustedInput;
        RlpReader reader = new(receiptsData);
        if (TrySkipCompactPrefix(ref reader, out _))
        {
            // The compact list holds a final empty item, which is a missing receipt, as one more item.
            return DecodeCompact(ref reader, RlpBehaviors.Storage | RlpBehaviors.AllowExtraBytes | trusted);
        }

        try
        {
            return DecodeArrayAllowingMissingReceipts(ref reader, RlpBehaviors.Storage | trusted);
        }
        catch (RlpReaderException)
        {
            reader = new RlpReader(receiptsData);
            return DecodeArrayAllowingMissingReceipts(ref reader, trusted);
        }
    }

    private static TxReceipt?[] DecodeArrayAllowingMissingReceipts(ref RlpReader reader, RlpBehaviors rlpBehaviors)
    {
        RlpReader items = reader.EnterSequence(out int itemLength);
        TxReceipt?[] result = new TxReceipt?[items.CountItems(RlpLimit.DefaultLimit)];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = Decoder.Decode(ref items, rlpBehaviors);
        }

        reader.ExitSequence(in items, itemLength, rlpBehaviors);
        return result;
    }

    /// <summary>Decodes the receipt list of a compact value, after its prefix.</summary>
    /// <returns>The receipts, with <see langword="null"/> for an empty item.</returns>
    private static TxReceipt?[] DecodeCompact(ref RlpReader reader, RlpBehaviors rlpBehaviors)
    {
        try
        {
            return (rlpBehaviors & RlpBehaviors.TrustedInput) != 0
                ? DecodeCompact<OffFlag>(ref reader, rlpBehaviors)
                : DecodeCompact<OnFlag>(ref reader, rlpBehaviors);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            ThrowRlpException(e);
            return default;
        }
    }

    private static TxReceipt?[] DecodeCompact<TCanonicalChecks>(ref RlpReader reader, RlpBehaviors rlpBehaviors) where TCanonicalChecks : struct, IFlag
    {
        RlpReader items = EnterCompactReceiptList<TCanonicalChecks>(ref reader);
        TxReceipt?[] result = new TxReceipt?[items.CountItems<TCanonicalChecks>(RlpLimit.DefaultLimit)];
        // A span store has no covariance check, unlike an array store of a reference type.
        Span<TxReceipt?> span = result;
        for (int i = 0; i < span.Length; i++)
        {
            span[i] = CompactReceiptStorageDecoder.Decode<TCanonicalChecks>(ref items, rlpBehaviors);
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
        catch (RlpReaderException)
        {
            context = new RlpReader(receiptData);
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
        if (!reader.HasNext) return false;

        switch (reader.PeekByte())
        {
            case CompactEncoding:
                reader.ReadRaw(1);
                return true;
            case CompactEncodingWithTxHashes:
                reader.ReadRaw(1);
                ReadOnlySpan<byte> hashes = reader.ReadBytes();
                if (hashes.Length % Hash256.Size != 0)
                {
                    throw new RlpReaderException($"Stored transaction hashes length {hashes.Length} is not a multiple of {Hash256.Size}.");
                }

                txHashes = hashes;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Reads the receipt list of a compact value, which <see cref="TrySkipCompactPrefix"/> leaves the reader at.</summary>
    /// <remarks>
    /// The compact list declares one byte less than its receipts take, so its length is not checked as a sequence
    /// length: the list ends one byte after its declared end, or at the end of the value.
    /// </remarks>
    /// <returns>A reader over the receipts of the list.</returns>
    public static RlpReader EnterCompactReceiptList(scoped ref RlpReader reader) => EnterCompactReceiptList<OnFlag>(ref reader);

    private static RlpReader EnterCompactReceiptList<TCanonicalChecks>(scoped ref RlpReader reader) where TCanonicalChecks : struct, IFlag
    {
        RlpReader declared = reader.EnterSequence<TCanonicalChecks>(out int itemLength);
        int headerLength = itemLength - declared.Remaining;
        reader.ReadRaw(headerLength);
        return new RlpReader(reader.ReadRaw(Math.Min(declared.Remaining + 1, reader.Remaining)));
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
        RlpWriter writer = RlpWriter.ContentWriter(result);
        writer.WriteByte(CompactEncodingWithTxHashes);
        writer.StartByteArray(txHashes.Length, false);
        writer.WriteEncoded(txHashes);
        writer.WriteEncoded(receipts);
        Rlp.CheckLength(in writer, result.Length);
        return result;
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
